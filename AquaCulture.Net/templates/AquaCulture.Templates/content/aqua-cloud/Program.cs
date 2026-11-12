// AquaCulture.Net multi-tenant cloud API — made by Gravicode Studios, led by Kang Fadhil.
// One database per tenant (AquaTenants), API keys per tenant (X-Api-Key), an admin key for tenant management (AQUA_ADMIN_KEY).
using System.Security.Cryptography;
using System.Text;
using AquaCulture.AI.Agent;
using AquaCulture.Core.Species;
using AquaCulture.Data;
using AquaCulture.IoT;
using AquaCulture.WaterQuality;

var builder = WebApplication.CreateBuilder(args);
var dataRoot = builder.Configuration["Aqua:DataRoot"] ?? Path.Combine(AppContext.BaseDirectory, "data");
var adminKey = builder.Configuration["AQUA_ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("AQUA_ADMIN_KEY");
builder.Services.AddSingleton(new AquaTenants(dataRoot));
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = AquaCulture.Core.AquaInfo.Version }));

// ── admin ──
bool IsAdmin(HttpRequest r) => adminKey is { Length: >= 16 } && r.Headers["X-Admin-Key"].ToString() is { Length: > 0 } k &&
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(adminKey));

app.MapPost("/admin/tenants", (CreateTenant body, HttpRequest req, AquaTenants tenants) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    try
    {
        var (t, key) = tenants.Create(body.Id, body.Name, body.Plan ?? "standard");
        return Results.Created($"/t/{t.Id}", new { t.Id, t.Name, apiKey = key, note = "Store the key now; it is not shown again." });
    }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = e.Message }); }
});
app.MapGet("/admin/tenants", (HttpRequest req, AquaTenants tenants) =>
    IsAdmin(req) ? Results.Ok(tenants.List().Select(t => new { t.Id, t.Name, t.Plan, t.CreatedUtc, t.Disabled })) : Results.Unauthorized());

// ── tenant API ──
var api = app.MapGroup("/t/{tenant}").AddEndpointFilter(async (ctx, next) =>
{
    var tenants = ctx.HttpContext.RequestServices.GetRequiredService<AquaTenants>();
    string tenant = ctx.HttpContext.Request.RouteValues["tenant"]?.ToString() ?? "";
    string key = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
    return AquaTenants.IsValidId(tenant) && tenants.Authenticate(tenant, key) ? await next(ctx) : Results.Unauthorized();
});

api.MapPost("/readings", async (string tenant, ReadingIn[] rows, AquaTenants tenants) =>
{
    var readings = new List<SensorReading>();
    foreach (var r in rows)
    {
        if (!SensorReading.TryParseKind(r.Kind, out var kind)) return Results.BadRequest(new { error = $"Unknown kind '{r.Kind}'." });
        readings.Add(new SensorReading(r.Device, r.Pond, kind, r.Value, (r.Time ?? DateTime.UtcNow).ToUniversalTime(), r.Unit ?? SensorReading.DefaultUnit(kind)));
    }
    int n = await tenants.Store(tenant).AppendReadingsAsync(readings);
    return Results.Accepted(value: new { stored = n });
});

api.MapGet("/ponds/{pond}/readings", async (string tenant, string pond, string? kind, int? hours, AquaTenants tenants) =>
{
    SensorKind? k = kind is null ? null : SensorReading.TryParseKind(kind, out var kk) ? kk : throw new BadHttpRequestException($"Unknown kind '{kind}'.");
    var from = DateTime.UtcNow.AddHours(-(hours ?? 24));
    var rows = await tenants.Store(tenant).QueryReadingsAsync(pond, k, from);
    return Results.Ok(rows.Select(r => new { r.DeviceId, kind = r.Kind.ToString(), r.Value, r.Unit, time = r.Timestamp }));
});

// AquaAgent grounded in the pond's latest readings (offline rule-based unless AQUA_LLM_* is configured)
api.MapPost("/ponds/{pond}/ask", async (string tenant, string pond, Question q, AquaTenants tenants) =>
{
    var store = tenants.Store(tenant);
    var latest = (await store.QueryReadingsAsync(pond, null, DateTime.UtcNow.AddHours(-6))).GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Last().Value);
    double? V(SensorKind k) => latest.TryGetValue(k, out var v) ? v : null;
    var ctx = new AquaContext
    {
        PondName = pond, Species = SpeciesCatalog.Get(q.Species ?? "SPECIES_ID"),
        Water = new WaterSample { TemperatureC = V(SensorKind.Temperature), DissolvedOxygen = V(SensorKind.DissolvedOxygen), Ph = V(SensorKind.Ph),
            SalinityPpt = V(SensorKind.Salinity), Ammonia = V(SensorKind.Ammonia) },
    };
    if (q.AbwG is { } abw) ctx.AbwG = abw;
    if (q.Population is { } pop) ctx.Population = pop;
    var answer = await new AquaAgent(ctx).AskAsync(q.Text);
    return Results.Ok(new { answer.Text, answer.ToolsUsed, answer.Model, answer.Language });
});

app.Run();

record CreateTenant(string Id, string Name, string? Plan);
record ReadingIn(string Device, string Pond, string Kind, double Value, DateTime? Time, string? Unit);
record Question(string Text, string? Species, double? AbwG, long? Population);
