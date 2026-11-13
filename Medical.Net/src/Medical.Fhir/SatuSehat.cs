using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using MedicalBuilder = Medical.Core.MedicalBuilder;
using CodeSystems = Medical.Core.CodeSystems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.Fhir;

public enum SatuSehatEnvironment { Sandbox, Production }

/// <summary>
/// Connection settings for SATUSEHAT, Indonesia's national health-data exchange (Kementerian Kesehatan RI), which is FHIR R4 based.
/// Credentials come from the SATUSEHAT developer portal; never hard-code them.
/// </summary>
public sealed record SatuSehatOptions
{
    public SatuSehatEnvironment Environment { get; init; } = SatuSehatEnvironment.Sandbox;
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    /// <summary>Organization id registered in SATUSEHAT (used in identifier systems).</summary>
    public string OrganizationId { get; init; } = "";

    public string Host => Environment == SatuSehatEnvironment.Production ? "https://api-satusehat.kemkes.go.id" : "https://api-satusehat-stg.dto.kemkes.go.id";
    public string AuthUrl => $"{Host}/oauth2/v1/accesstoken?grant_type=client_credentials";
    public string FhirBaseUrl => $"{Host}/fhir-r4/v1";

    /// <summary>Reads <c>SATUSEHAT_CLIENT_ID</c>, <c>SATUSEHAT_CLIENT_SECRET</c>, <c>SATUSEHAT_ORG_ID</c> and <c>SATUSEHAT_ENV</c> (sandbox|production).</summary>
    public static SatuSehatOptions FromEnvironment() => new()
    {
        ClientId = global::System.Environment.GetEnvironmentVariable("SATUSEHAT_CLIENT_ID") ?? "",
        ClientSecret = global::System.Environment.GetEnvironmentVariable("SATUSEHAT_CLIENT_SECRET") ?? "",
        OrganizationId = global::System.Environment.GetEnvironmentVariable("SATUSEHAT_ORG_ID") ?? "",
        Environment = string.Equals(global::System.Environment.GetEnvironmentVariable("SATUSEHAT_ENV"), "production", StringComparison.OrdinalIgnoreCase)
            ? SatuSehatEnvironment.Production : SatuSehatEnvironment.Sandbox,
    };
}

/// <summary>SATUSEHAT identifier systems.</summary>
public static class SatuSehatSystems
{
    public const string Nik = CodeSystems.Nik;
    public const string IhsNumber = "https://fhir.kemkes.go.id/id/ihs-number";
    public static string Organization(string orgId) => $"http://sys-ids.kemkes.go.id/organization/{orgId}";
    public static string Encounter(string orgId) => $"http://sys-ids.kemkes.go.id/encounter/{orgId}";
    public static string Observation(string orgId) => $"http://sys-ids.kemkes.go.id/observation/{orgId}";
    public static string Location(string orgId) => $"http://sys-ids.kemkes.go.id/location/{orgId}";
}

/// <summary>
/// Minimal SATUSEHAT REST client: OAuth2 client-credentials token (cached until expiry), patient lookup by NIK and
/// resource create/read. Resources are exchanged as FHIR JSON via the Firely serializer.
/// </summary>
public sealed class SatuSehatClient(HttpClient http, SatuSehatOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public SatuSehatOptions Options { get; } = options;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_token is not null && _clock.GetUtcNow() < _expires) return _token;
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _clock.GetUtcNow() < _expires) return _token;
            if (Options.ClientId.Length == 0 || Options.ClientSecret.Length == 0)
                throw new InvalidOperationException("SATUSEHAT ClientId/ClientSecret are not configured.");
            using var content = new FormUrlEncodedContent([new("client_id", Options.ClientId), new("client_secret", Options.ClientSecret)]);
            using var res = await http.PostAsync(Options.AuthUrl, content, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            _token = doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidDataException("No access_token in response.");
            double seconds = doc.RootElement.TryGetProperty("expires_in", out var e)
                ? e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.Parse(e.GetString() ?? "3600", CultureInfo.InvariantCulture)
                : 3600;
            _expires = _clock.GetUtcNow().AddSeconds(seconds - 60);
            return _token;
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>GET Patient?identifier=https://fhir.kemkes.go.id/id/nik|{nik}</summary>
    public Task<Bundle> SearchPatientByNikAsync(string nik, CancellationToken ct = default) =>
        SendAsync<Bundle>(HttpMethod.Get, $"Patient?identifier={Uri.EscapeDataString($"{SatuSehatSystems.Nik}|{nik}")}", null, ct);

    public Task<T> ReadAsync<T>(string id, CancellationToken ct = default) where T : Resource, new() =>
        SendAsync<T>(HttpMethod.Get, $"{new T().TypeName}/{id}", null, ct);

    public Task<T> CreateAsync<T>(T resource, CancellationToken ct = default) where T : Resource =>
        SendAsync<T>(HttpMethod.Post, resource.TypeName, resource, ct);

    /// <summary>Builds a request without sending it (useful for logging / dry runs).</summary>
    public HttpRequestMessage BuildRequest(HttpMethod method, string relative, Resource? body, string token)
    {
        var req = new HttpRequestMessage(method, $"{Options.FhirBaseUrl}/{relative}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) req.Content = new StringContent(FhirJson.Serialize(body, false), Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string relative, Resource? body, CancellationToken ct) where T : Resource
    {
        string token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        using var req = BuildRequest(method, relative, body, token);
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            string detail = FhirJson.TryParse(json, out var r, out _) && r is OperationOutcome oo
                ? string.Join("; ", oo.Issue.Select(i => i.Diagnostics ?? i.Details?.Text)) : json;
            throw new HttpRequestException($"SATUSEHAT {method} {relative} failed ({(int)res.StatusCode}): {detail}", null, res.StatusCode);
        }
        return FhirJson.Parse<T>(json);
    }

    /// <summary>Adds SATUSEHAT-required identifiers/references to an encounter produced by <see cref="FhirMapper"/>.</summary>
    public Encounter PrepareEncounter(Encounter e, string ihsPatientId, string patientName, string? practitionerIhs = null, string? locationId = null)
    {
        var copy = (Encounter)e.DeepCopy();
        copy.Identifier = [new Identifier(SatuSehatSystems.Encounter(Options.OrganizationId), e.Id)];
        copy.Subject = new ResourceReference($"Patient/{ihsPatientId}", patientName);
        copy.ServiceProvider = new ResourceReference($"Organization/{Options.OrganizationId}");
        if (practitionerIhs is not null)
            copy.Participant = [new Encounter.ParticipantComponent { Individual = new ResourceReference($"Practitioner/{practitionerIhs}") }];
        if (locationId is not null)
            copy.Location = [new Encounter.LocationComponent { Location = new ResourceReference($"Location/{locationId}") }];
        copy.StatusHistory = [new Encounter.StatusHistoryComponent { Status = copy.Status ?? Encounter.EncounterStatus.Arrived, Period = copy.Period ?? new Period() }];
        return copy;
    }
}

public static class FhirBuilderExtensions
{
    /// <summary>Registers <see cref="FhirMapper"/>, a singleton <see cref="InMemoryFhirStore"/> and (optionally) <see cref="SatuSehatClient"/>.</summary>
    public static MedicalBuilder AddFhir(this MedicalBuilder builder, FhirMappingOptions? mapping = null, SatuSehatOptions? satuSehat = null) =>
        builder.TryAddModule("fhir", s =>
        {
            s.TryAddSingleton(new FhirMapper(mapping));
            s.TryAddSingleton(sp => new InMemoryFhirStore(sp.GetService<TimeProvider>()));
            if (satuSehat is not null)
            {
                s.AddHttpClient("satusehat");
                s.TryAddSingleton(sp => new SatuSehatClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("satusehat"), satuSehat, sp.GetService<TimeProvider>()));
            }
        });
}
