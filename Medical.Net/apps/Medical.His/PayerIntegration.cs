using Medical.Bpjs.Crypto;
using Medical.Bpjs.InaCbg;
using Medical.Bpjs.VClaim;
using Medical.Fhir;
using Medical.Hospital;
using Medical.Hospital.Integration;
using Microsoft.EntityFrameworkCore;

namespace Medical.His;

/// <summary>
/// Payer and national-platform clients for the HIS, registered only when their configuration is complete:
/// <list type="bullet">
/// <item><c>His:Bpjs:VClaim:{ConsId,Secret,UserKey,Environment}</c> → <see cref="VClaimClient"/> (Environment: Development | Production).
/// The hospital's BPJS provider code for SEPs is read by the registration page from <c>His:Bpjs:VClaim:PpkCode</c>.</item>
/// <item><c>His:Bpjs:EKlaim:{Url,Key}</c> → <see cref="EKlaimClient"/> (Url = <c>http://&lt;server&gt;/E-Klaim/ws.php</c>, Key = 64 hex characters).</item>
/// <item><c>His:SatuSehat:{ClientId,ClientSecret,OrganizationId,Environment}</c> → <see cref="SatuSehatClient"/> and
/// <see cref="SatuSehatPublisherOptions"/> (<c>His:SatuSehat:Locations:{departmentCode}</c>, <c>DefaultLocationId</c>, <c>MaxAttempts</c>);
/// with <c>His:SatuSehat:AutoSubmit=true</c> a background sweeper also queues finished visits and retries failed submissions
/// every <c>SweepSeconds</c> (default 300).</item>
/// </list>
/// Nothing is registered for a section that is missing, so pages resolve these services optionally and show a
/// "not configured" message. Credentials belong in user secrets or environment variables, never in appsettings.json.
/// </summary>
public static class PayerIntegration
{
    private static string? Value(IConfiguration c, string key) => c[key] is { Length: > 0 } v ? v.Trim() : null;

    public static IServiceCollection AddHisPayerIntegration(this IServiceCollection s, IConfiguration c)
    {
        if (Value(c, "His:Bpjs:VClaim:ConsId") is { } consId && Value(c, "His:Bpjs:VClaim:Secret") is { } secret && Value(c, "His:Bpjs:VClaim:UserKey") is { } userKey)
        {
            var env = string.Equals(c["His:Bpjs:VClaim:Environment"], "Production", StringComparison.OrdinalIgnoreCase) ? BpjsEnvironment.Production : BpjsEnvironment.Development;
            var credentials = new BpjsCredentials(consId, secret, userKey);
            s.AddHttpClient("his-bpjs-vclaim", h => h.Timeout = TimeSpan.FromSeconds(60));
            s.AddSingleton(sp => new VClaimClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("his-bpjs-vclaim"), credentials, env, sp.GetService<TimeProvider>()));
        }

        if (Value(c, "His:Bpjs:EKlaim:Url") is { } url && Value(c, "His:Bpjs:EKlaim:Key") is { } key)
        {
            s.AddHttpClient("his-bpjs-eklaim", h => h.Timeout = TimeSpan.FromSeconds(120));
            s.AddSingleton(sp => new EKlaimClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("his-bpjs-eklaim"), url, key));
        }

        if (Value(c, "His:SatuSehat:ClientId") is { } clientId && Value(c, "His:SatuSehat:ClientSecret") is { } clientSecret)
        {
            var options = new SatuSehatOptions
            {
                ClientId = clientId, ClientSecret = clientSecret, OrganizationId = Value(c, "His:SatuSehat:OrganizationId") ?? "",
                Environment = string.Equals(c["His:SatuSehat:Environment"], "Production", StringComparison.OrdinalIgnoreCase) ? SatuSehatEnvironment.Production : SatuSehatEnvironment.Sandbox,
            };
            s.AddHttpClient("his-satusehat", h => h.Timeout = TimeSpan.FromSeconds(100)).AddHttpMessageHandler(() => new Medical.Fhir.Http.RetryHandler());
            s.AddSingleton(sp => new SatuSehatClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("his-satusehat"), options, sp.GetService<TimeProvider>()));
            var locations = c.GetSection("His:SatuSehat:Locations").GetChildren().Where(x => x.Value is { Length: > 0 })
                .ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
            s.AddSingleton(new SatuSehatPublisherOptions
            {
                LocationIds = locations, DefaultLocationId = Value(c, "His:SatuSehat:DefaultLocationId"),
                MaxAttempts = c.GetValue("His:SatuSehat:MaxAttempts", 5),
            });
            if (c.GetValue("His:SatuSehat:AutoSubmit", false)) s.AddHostedService<SatuSehatSweeper>();
        }
        return s;
    }

    /// <summary>The BPJS workflow over a context, with whichever clients are configured (null otherwise).</summary>
    public static BpjsWorkflowService BpjsWorkflow(this IServiceProvider sp, HospitalDbContext db, TimeProvider clock) =>
        new(db, sp.GetService<VClaimClient>(), sp.GetService<EKlaimClient>(), clock);

    /// <summary>
    /// Sends a just-finished or discharged visit to SATUSEHAT when it is configured. A failure never blocks the clinical
    /// action: the submission stays in the outbox (status Failed, with the error) for the sweeper or a manual retry.
    /// </summary>
    public static async Task SendToSatuSehatAsync(this IServiceProvider sp, HospitalDbContext db, TimeProvider clock, string visitId)
    {
        if (sp.SatuSehatPublisher(db, clock) is not { } publisher) return;
        try { await publisher.PublishVisitAsync(visitId); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or PayerIntegrationException)
        {
            sp.GetService<ILoggerFactory>()?.CreateLogger("SatuSehat").LogWarning(e, "SATUSEHAT submission of visit {Visit} deferred", visitId);
        }
    }

    /// <summary>The SATUSEHAT publisher over a context, or null when SATUSEHAT is not configured.</summary>
    public static SatuSehatPublisher? SatuSehatPublisher(this IServiceProvider sp, HospitalDbContext db, TimeProvider clock) =>
        sp.GetService<SatuSehatClient>() is { } client ? new SatuSehatPublisher(db, client, sp.GetService<SatuSehatPublisherOptions>(), clock) : null;
}

/// <summary>Queues visits finished in every active tenant and sends pending / failed SATUSEHAT submissions on a timer.</summary>
internal sealed class SatuSehatSweeper(DbContextOptions<HospitalDbContext> dbOptions, SatuSehatClient client, SatuSehatPublisherOptions options,
    TimeProvider clock, IConfiguration config, ILogger<SatuSehatSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, config.GetValue("His:SatuSehat:SweepSeconds", 300)));
        var since = clock.GetUtcNow().UtcDateTime.AddHours(-Math.Max(0, config.GetValue("His:SatuSehat:LookbackHours", 24)));
        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                List<string> tenants;
                await using (var root = new HospitalDbContext(dbOptions, new FixedTenantContext("")))
                    tenants = await root.Tenants.Where(t => t.Active).Select(t => t.Id).ToListAsync(ct);
                foreach (var tenant in tenants)
                {
                    await using var db = new HospitalDbContext(dbOptions, new FixedTenantContext(tenant, "satusehat"));
                    var publisher = new SatuSehatPublisher(db, client, options, clock);
                    int queued = await publisher.EnqueueFinishedVisitsAsync(since, ct);
                    var run = await publisher.RetryPendingAsync(ct: ct);
                    if (queued + run.Sent + run.Failed > 0)
                        log.LogInformation("SATUSEHAT {Tenant}: queued {Queued}, sent {Sent}, failed {Failed}", tenant, queued, run.Sent, run.Failed);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "SATUSEHAT sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
