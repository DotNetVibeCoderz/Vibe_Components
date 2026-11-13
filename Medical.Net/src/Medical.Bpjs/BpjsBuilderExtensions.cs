using Medical.Bpjs.Crypto;
using Medical.Bpjs.InaCbg;
using Medical.Bpjs.VClaim;
using Medical.Core;
using Medical.Fhir.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.Bpjs;

/// <summary>Configuration for <see cref="BpjsBuilderExtensions.AddBpjs"/>.</summary>
public sealed class BpjsOptions
{
    public BpjsCredentials Credentials { get; set; } = BpjsCredentials.FromEnvironment();
    public BpjsEnvironment Environment { get; set; } = BpjsEnvironment.Development;
    /// <summary>E-Klaim web-service URL, e.g. <c>http://10.0.0.5/E-Klaim/ws.php</c>.</summary>
    public string? EKlaimUrl { get; set; }
    /// <summary>E-Klaim encryption key (64 hex characters) from the E-Klaim setup page.</summary>
    public string? EKlaimKey { get; set; }
    public string? PCareUsername { get; set; }
    public string? PCarePassword { get; set; }
}

public static class BpjsBuilderExtensions
{
    /// <summary>Registers VClaim, PCare (when credentials are set) and E-Klaim (when URL and key are set) with retry/backoff.</summary>
    public static MedicalBuilder AddBpjs(this MedicalBuilder builder, Action<BpjsOptions>? configure = null) =>
        builder.TryAddModule("bpjs", s =>
        {
            var o = new BpjsOptions();
            configure?.Invoke(o);
            s.TryAddSingleton(o);
            s.AddHttpClient("bpjs").AddHttpMessageHandler(() => new RetryHandler());
            s.TryAddTransient(sp => new VClaimClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("bpjs"), o.Credentials, o.Environment, sp.GetService<TimeProvider>()));
            if (o.PCareUsername is { } u && o.PCarePassword is { } p)
                s.TryAddTransient(sp => new PCareClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("bpjs"), o.Credentials, u, p, o.Environment, clock: sp.GetService<TimeProvider>()));
            if (o.EKlaimUrl is { } url && o.EKlaimKey is { } key)
                s.TryAddTransient(sp => new EKlaimClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("bpjs"), url, key));
        });
}
