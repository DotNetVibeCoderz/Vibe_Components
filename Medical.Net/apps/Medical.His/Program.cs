using System.Security.Claims;
using Medical.Fhir;
using Medical.His;
using Medical.His.Components;
using Medical.Hospital;
using Medical.Sync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

// Medical.Net HIS reference app — Made by Gravicode Studios, led by Kang Fadhil.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/login"; o.Cookie.Name = "medicalnet.his"; o.ExpireTimeSpan = TimeSpan.FromHours(10); });
builder.Services.AddAuthorization();

// Database: SQLite by default (App_Data/his.db); set ConnectionStrings:His + His:Provider=SqlServer for SQL Server.
string provider = builder.Configuration["His:Provider"] ?? "Sqlite";
string cs = builder.Configuration.GetConnectionString("His") ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "App_Data", "his.db")}";
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
var dbOptions = provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
    ? new DbContextOptionsBuilder<HospitalDbContext>().UseSqlServer(cs).Options
    : new DbContextOptionsBuilder<HospitalDbContext>().UseSqlite(cs).Options;
builder.Services.AddSingleton(dbOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MonitoringHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringHub>());
builder.Services.AddScoped<HisSession>();

var app = builder.Build();
await HospitalSeeder.SeedAsync(dbOptions, TimeProvider.System, builder.Configuration.GetValue("His:SeedPatients", 40));

bool demoMode = app.Configuration.GetValue("His:DemoLogin", true);
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---- Sign-in (demo users without passwords; His:DemoLogin=false switches both endpoints off — plug in OpenID Connect / your IdP) ----
app.MapPost("/account/login", async (HttpContext http, DbContextOptions<HospitalDbContext> opts) =>
{
    if (!demoMode) return Results.NotFound();
    var form = await http.Request.ReadFormAsync();
    return await SignInAsync(http, opts, form["tenant"].ToString(), form["user"].ToString(), form["lang"].ToString(), form["returnUrl"].ToString());
}).DisableAntiforgery();
app.MapGet("/account/demo", (HttpContext http, DbContextOptions<HospitalDbContext> opts, string tenant, string user, string? lang, string? returnUrl) =>
    demoMode ? SignInAsync(http, opts, tenant, user, lang, returnUrl) : Task.FromResult(Results.NotFound()));
app.MapGet("/account/logout", async (HttpContext http) => { await http.SignOutAsync(); return Results.Redirect("/login"); });

static async Task<IResult> SignInAsync(HttpContext http, DbContextOptions<HospitalDbContext> opts, string tenant, string user, string? lang, string? returnUrl)
{
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant));
    var staff = await db.Staff.FirstOrDefaultAsync(s => s.UserName == user);
    if (staff is null || !await db.Tenants.AnyAsync(t => t.Id == tenant && t.Active)) return Results.Redirect("/login?error=1");
    var id = new ClaimsIdentity([
        new Claim(ClaimTypes.Name, staff.UserName), new Claim("display", staff.Name), new Claim(ClaimTypes.Role, staff.Role.ToString()),
        new Claim("tenant", tenant), new Claim("lang", lang is "en" ? "en" : "id"), new Claim("staff", staff.Id),
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(new ClaimsPrincipal(id));
    db.AuditEvent(Medical.Core.AuditAction.Login, "Session", null, staff.UserName);
    await db.SaveChangesAsync();
    // Local paths only: "//host" and "/\host" would be protocol-relative redirects to another site.
    bool local = returnUrl is { Length: > 1 } r && r[0] == '/' && r[1] != '/' && r[1] != '\\';
    return Results.Redirect(local ? returnUrl! : $"/{tenant}");
}

// ---- Ward-round sync API (device key per tenant; pulls scoped to a ward) ----
// Keys come from His:DeviceKeys:{tenant}; the shared demo key is accepted only in demo mode.
async ValueTask<object?> DeviceKeyFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var http = ctx.HttpContext;
    string tenant = http.GetRouteValue("tenant")?.ToString() ?? "";
    string? expected = app.Configuration[$"His:DeviceKeys:{tenant}"] ?? (demoMode ? "demo-device-key" : null);
    string given = http.Request.Headers["X-Device-Key"].ToString();
    bool ok = expected is not null && given.Length == expected.Length &&
              System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(expected));
    return ok ? await next(ctx) : Results.Unauthorized();
}
var sync = app.MapGroup("/api/{tenant}/sync").AddEndpointFilter(DeviceKeyFilter);
sync.MapPost("/push", async (string tenant, SyncPushRequest req, DbContextOptions<HospitalDbContext> opts) =>
{
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, $"device:{req.NodeId}", StaffRole.Nurse));
    return Results.Json(RoundsSync.Server(db).Push(req), SyncJson.Options);
});
sync.MapPost("/pull", async (string tenant, string? ward, SyncPullRequest req, DbContextOptions<HospitalDbContext> opts) =>
{
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, $"device:{req.NodeId}", StaffRole.Nurse));
    // A device always pulls one ward; a missing or unknown ward is refused rather than widened to the whole hospital.
    var wardDept = ward is null ? null : await db.Departments.FirstOrDefaultAsync(d => d.Code == ward && (d.Kind == DepartmentKind.Ward || d.Kind == DepartmentKind.Icu));
    if (wardDept is null) return Results.BadRequest(new { error = "Unknown or missing ?ward= code." });
    return Results.Json(RoundsSync.Server(db).Pull(req, wardDept.Id), SyncJson.Options);
});
sync.MapGet("/wards", async (string tenant, DbContextOptions<HospitalDbContext> opts) =>
{
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant));
    return Results.Json(await db.Departments.Where(d => d.Kind == DepartmentKind.Ward || d.Kind == DepartmentKind.Icu).Select(d => new { d.Code, d.NameId }).ToListAsync());
});

// ---- FHIR export and dashboard (signed-in users of the tenant) ----
app.MapGet("/api/{tenant}/fhir/Patient/{id}/$summary", async (string tenant, string id, HttpContext http, DbContextOptions<HospitalDbContext> opts) =>
{
    if (http.User.FindFirst("tenant")?.Value != tenant) return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, http.User.Identity!.Name!));
    var record = await HisRecords.ToPatientRecordAsync(db, id);
    db.AuditEvent(Medical.Core.AuditAction.Export, "Patient/$summary", id);
    await db.SaveChangesAsync();
    return Results.Text(FhirJson.Serialize(new FhirMapper().ToPatientSummary(record)), "application/fhir+json");
}).RequireAuthorization();
app.MapGet("/api/{tenant}/dashboard", async (string tenant, HttpContext http, DbContextOptions<HospitalDbContext> opts) =>
{
    if (http.User.FindFirst("tenant")?.Value != tenant) return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, http.User.Identity!.Name!));
    return Results.Json(await new DashboardService(db).SnapshotAsync());
}).RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok", Medical.Core.MedicalInfo.Version }));

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
