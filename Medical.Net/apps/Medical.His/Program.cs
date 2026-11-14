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
    .AddCookie(o => { o.LoginPath = "/login"; o.Cookie.Name = "medicalnet.his"; o.ExpireTimeSpan = TimeSpan.FromHours(10); })
    .AddHisOidc(builder.Configuration);                    // OpenID Connect when His:Oidc:Authority is set
builder.Services.AddAuthorization();

// Database: SQLite by default (App_Data/his.db); His:Provider=SqlServer + ConnectionStrings:His for SQL Server.
// Database-per-tenant: His:Tenants:{tenantId}:ConnectionString routes that hospital to its own database
// (ConnectionStrings:His then holds the tenant catalog). Migrations run at start-up for every database.
var provider = string.Equals(builder.Configuration["His:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase) ? HospitalProvider.SqlServer : HospitalProvider.Sqlite;
string cs = builder.Configuration.GetConnectionString("His") ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "App_Data", "his.db")}";
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
var perTenant = builder.Configuration.GetSection("His:Tenants").GetChildren()
    .Where(t => !string.IsNullOrWhiteSpace(t["ConnectionString"])).ToDictionary(t => t.Key, t => t["ConnectionString"]!);
var dbOptions = HospitalDatabase.Options(provider, cs, perTenant);
builder.Services.AddSingleton(dbOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MonitoringHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringHub>());
builder.Services.AddScoped<HisSession>();
builder.Services.AddHisPayerIntegration(builder.Configuration);   // BPJS VClaim / E-Klaim, SATUSEHAT — only when configured
builder.Services.AddSingleton<Medical.Core.IMedicalEventBus, Medical.Core.InMemoryMedicalEventBus>();
builder.Services.AddHisInterfaces(builder.Configuration);       // DICOM worklist SCP (His:Worklist:Port), HL7 ADT feed (His:Adt:Host/Port)

var app = builder.Build();
await HospitalSeeder.SeedAsync(dbOptions, TimeProvider.System, builder.Configuration.GetValue("His:SeedPatients", 40));

bool demoMode = app.Configuration.GetValue("His:DemoLogin", true);
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
    app.UseHttpsRedirection();
    if (demoMode) app.Logger.LogWarning("His:DemoLogin is on outside Development: anyone can sign in without a password. Set His:DemoLogin=false and configure His:Oidc.");
}
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---- Sign-in: OpenID Connect (/account/sso) and, in demo mode only, password-less demo users ----
app.MapGet("/account/sso", (string? tenant, string? lang, string? returnUrl) =>
    HisAuth.OidcConfigured(app.Configuration)
        ? Results.Challenge(new AuthenticationProperties(new Dictionary<string, string?> { ["tenant"] = tenant ?? "", ["lang"] = lang ?? "id" })
            { RedirectUri = HisAuth.LocalOr(returnUrl, tenant is { Length: > 0 } ? $"/{tenant}" : "/") }, [HisAuth.OidcScheme])
        : Results.NotFound());
app.MapPost("/account/login", async (HttpContext http, DbContextOptions<HospitalDbContext> opts) =>
{
    if (!demoMode) return Results.NotFound();
    var form = await http.Request.ReadFormAsync();
    return await DemoSignInAsync(http, opts, form["tenant"].ToString(), form["user"].ToString(), form["lang"].ToString(), form["returnUrl"].ToString());
}).DisableAntiforgery();
app.MapGet("/account/demo", (HttpContext http, DbContextOptions<HospitalDbContext> opts, string tenant, string user, string? lang, string? returnUrl) =>
    demoMode ? DemoSignInAsync(http, opts, tenant, user, lang, returnUrl) : Task.FromResult(Results.NotFound()));
app.MapGet("/account/logout", async (HttpContext http) => { await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Redirect("/login"); });

static async Task<IResult> DemoSignInAsync(HttpContext http, DbContextOptions<HospitalDbContext> opts, string tenant, string user, string? lang, string? returnUrl)
{
    var principal = await HisAuth.PrincipalAsync(opts, tenant, user, lang, "demo");
    if (principal is null) return Results.Redirect("/login?error=1");
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Redirect(HisAuth.LocalOr(returnUrl, $"/{tenant}"));
}

// ---- Ward-round sync API ----
// Enrolled devices send X-Device-Token (per-device, hashed server-side, bound to one ward). The legacy shared key
// (X-Device-Key from His:DeviceKeys:{tenant}, or the demo key in demo mode) still works but is not ward-bound.
async ValueTask<object?> DeviceAuthFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var http = ctx.HttpContext;
    string tenant = http.GetRouteValue("tenant")?.ToString() ?? "";
    string? token = http.Request.Headers["X-Device-Token"].FirstOrDefault();
    if (token is not null)
    {
        await using var db = new HospitalDbContext(http.RequestServices.GetRequiredService<DbContextOptions<HospitalDbContext>>(), new FixedTenantContext(tenant, "device-auth"));
        if (await new DeviceRegistry(db).AuthenticateAsync(token) is not { } reg) return Results.Unauthorized();
        http.Items["device"] = reg;
        return await next(ctx);
    }
    string? expected = app.Configuration[$"His:DeviceKeys:{tenant}"] ?? (demoMode ? "demo-device-key" : null);
    string given = http.Request.Headers["X-Device-Key"].ToString();
    bool ok = expected is not null && given.Length == expected.Length &&
              System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(expected));
    return ok ? await next(ctx) : Results.Unauthorized();
}
var sync = app.MapGroup("/api/{tenant}/sync").AddEndpointFilter(DeviceAuthFilter);
sync.MapPost("/push", async (string tenant, SyncPushRequest req, HttpContext http, DbContextOptions<HospitalDbContext> opts, MonitoringHub hub) =>
{
    var device = http.Items["device"] as DeviceRegistration;
    if (device is not null && req.NodeId != device.DeviceId) return Results.StatusCode(StatusCodes.Status403Forbidden);   // a token speaks only for its own device
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, $"device:{req.NodeId}", StaffRole.Nurse));
    var result = RoundsSync.Server(db, device?.WardId).Push(req);
    await NurseAssessmentsAsync(hub, tenant, req, result);
    return Results.Json(result, SyncJson.Options);
});
sync.MapPost("/pull", async (string tenant, string? ward, SyncPullRequest req, HttpContext http, DbContextOptions<HospitalDbContext> opts) =>
{
    var device = http.Items["device"] as DeviceRegistration;
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, $"device:{req.NodeId}", StaffRole.Nurse));
    // A device always pulls one ward; a missing or unknown ward is refused rather than widened to the whole hospital.
    var wardDept = ward is null ? null : await db.Departments.FirstOrDefaultAsync(d => d.Code == ward && (d.Kind == DepartmentKind.Ward || d.Kind == DepartmentKind.Icu));
    if (device is not null)
    {
        if (wardDept is not null && wardDept.Id != device.WardId) return Results.StatusCode(StatusCodes.Status403Forbidden);   // enrolled for another ward
        return Results.Json(RoundsSync.Server(db, device.WardId).Pull(req, device.WardId), SyncJson.Options);
    }
    if (wardDept is null) return Results.BadRequest(new { error = "Unknown or missing ?ward= code." });
    return Results.Json(RoundsSync.Server(db).Pull(req, wardDept.Id), SyncJson.Options);
});
sync.MapGet("/wards", async (string tenant, DbContextOptions<HospitalDbContext> opts) =>
{
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant));
    return Results.Json(await db.Departments.Where(d => d.Kind == DepartmentKind.Ward || d.Kind == DepartmentKind.Icu).Select(d => new { d.Code, d.NameId }).ToListAsync());
});

// Vitals taken on a tablet carry the nurse's ACVPU and oxygen: hand them to the live monitoring engine.
static Task NurseAssessmentsAsync(MonitoringHub hub, string tenant, SyncPushRequest req, SyncPushResult result)
{
    var accepted = result.Accepted.ToHashSet();
    foreach (var ch in req.Changes.Where(c => c.Type == Medical.Sync.Rounds.RoundsTypes.Vitals && c.Data is not null && accepted.Contains(c.Id)))
    {
        var v = System.Text.Json.JsonSerializer.Deserialize<Medical.Sync.Rounds.RoundsVitals>(ch.Data!, SyncJson.Options);
        if (v is null) continue;
        hub.ApplyNurseAssessment(tenant, v.PatientId, Enum.TryParse<Medical.Core.Consciousness>(v.Avpu, true, out var c) ? c : Medical.Core.Consciousness.Alert, v.OnOxygen);
    }
    return Task.CompletedTask;
}

// ---- Device enrolment (signed-in admins / nurses of the tenant): the token is returned once ----
app.MapPost("/api/{tenant}/devices", async (string tenant, DeviceEnrolmentRequest req, HttpContext http, DbContextOptions<HospitalDbContext> opts) =>
{
    if (http.User.FindFirst("tenant")?.Value != tenant || !(http.User.IsInRole(nameof(StaffRole.Admin)) || http.User.IsInRole(nameof(StaffRole.Nurse))))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, http.User.Identity!.Name!));
    try
    {
        var e = await new DeviceRegistry(db).EnrollAsync(req.DeviceId, req.Name ?? req.DeviceId, req.Ward);
        return Results.Json(new { e.Device.DeviceId, ward = e.WardCode, token = e.Token });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization().DisableAntiforgery();

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

/// <summary>Body of POST /api/{tenant}/devices.</summary>
public sealed record DeviceEnrolmentRequest(string DeviceId, string Ward, string? Name);

