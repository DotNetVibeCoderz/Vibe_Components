using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Medical.Fhir.Smart;

public enum SmartCompartment { Patient, User, System }

/// <summary>
/// A SMART on FHIR scope — v1 (<c>patient/Observation.read</c>, <c>user/*.write</c>, <c>system/*.*</c>) or v2
/// (<c>patient/Observation.rs</c>, <c>user/Condition.cruds</c>). Permissions are normalised to c, r, u, d, s.
/// </summary>
public sealed record SmartScope(SmartCompartment Compartment, string ResourceType, string Permissions)
{
    public static SmartScope? Parse(string scope)
    {
        int slash = scope.IndexOf('/'), dot = scope.LastIndexOf('.');
        if (slash <= 0 || dot <= slash) return null;
        var comp = scope[..slash] switch { "patient" => SmartCompartment.Patient, "user" => SmartCompartment.User, "system" => SmartCompartment.System, _ => (SmartCompartment?)null };
        if (comp is null) return null;
        string type = scope[(slash + 1)..dot], perm = scope[(dot + 1)..].Split('?')[0];
        string normalized = perm switch
        {
            "read" => "rs",
            "write" => "cud",
            "*" => "cruds",
            _ when perm.All(c => "cruds".Contains(c)) && perm.Length > 0 => perm,
            _ => "",
        };
        return normalized.Length == 0 ? null : new SmartScope(comp.Value, type, normalized);
    }

    public static IReadOnlyList<SmartScope> ParseAll(string? scopes) =>
        (scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<SmartScope>().ToArray();

    /// <summary>True when the scope grants <paramref name="permission"/> (c, r, u, d or s) on <paramref name="type"/>.</summary>
    public bool Allows(string type, char permission) => (ResourceType == "*" || ResourceType == type) && Permissions.Contains(permission);

    public override string ToString() => $"{Compartment.ToString().ToLowerInvariant()}/{ResourceType}.{Permissions}";
}

/// <summary>Contents of <c>/.well-known/smart-configuration</c>.</summary>
public sealed record SmartConfiguration
{
    [JsonPropertyName("issuer")] public string? Issuer { get; init; }
    [JsonPropertyName("authorization_endpoint")] public required string AuthorizationEndpoint { get; init; }
    [JsonPropertyName("token_endpoint")] public required string TokenEndpoint { get; init; }
    [JsonPropertyName("grant_types_supported")] public string[] GrantTypesSupported { get; init; } = [];
    [JsonPropertyName("scopes_supported")] public string[] ScopesSupported { get; init; } = [];
    [JsonPropertyName("response_types_supported")] public string[] ResponseTypesSupported { get; init; } = [];
    [JsonPropertyName("capabilities")] public string[] Capabilities { get; init; } = [];
    [JsonPropertyName("code_challenge_methods_supported")] public string[] CodeChallengeMethodsSupported { get; init; } = [];
    [JsonPropertyName("token_endpoint_auth_methods_supported")] public string[] TokenEndpointAuthMethodsSupported { get; init; } = [];
}

/// <summary>Token response including SMART launch context.</summary>
public sealed record SmartToken
{
    [JsonPropertyName("access_token")] public required string AccessToken { get; init; }
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("patient")] public string? Patient { get; init; }
    [JsonPropertyName("encounter")] public string? Encounter { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
}

/// <summary>An authorization request to send the user to, plus the secrets to keep for the callback.</summary>
public sealed record SmartAuthorizationRequest(Uri Url, string State, string CodeVerifier);

/// <summary>
/// SMART App Launch (standalone and EHR launch) and backend-services client: discovery, authorization URL with PKCE (S256),
/// code exchange, client-credentials tokens and an <see cref="HttpClient"/> handler that attaches the bearer token.
/// </summary>
public sealed class SmartClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SmartConfiguration> DiscoverAsync(string fhirBaseUrl, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"{fhirBaseUrl.TrimEnd('/')}/.well-known/smart-configuration", ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<SmartConfiguration>(Json, ct).ConfigureAwait(false)
               ?? throw new InvalidDataException("Empty SMART configuration.");
    }

    /// <summary>Builds the authorize URL. Pass <paramref name="launch"/> for an EHR launch; <paramref name="aud"/> is the FHIR base URL.</summary>
    public static SmartAuthorizationRequest CreateAuthorizationRequest(SmartConfiguration config, string clientId, string redirectUri, string scopes, string aud, string? launch = null)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["scope"] = scopes,
            ["state"] = state, ["aud"] = aud, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        };
        if (launch is not null) q["launch"] = launch;
        var url = config.AuthorizationEndpoint + (config.AuthorizationEndpoint.Contains('?') ? "&" : "?") +
                  string.Join('&', q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return new SmartAuthorizationRequest(new Uri(url), state, verifier);
    }

    public Task<SmartToken> ExchangeCodeAsync(SmartConfiguration config, string code, string clientId, string redirectUri, string codeVerifier, string? clientSecret = null, CancellationToken ct = default) =>
        TokenAsync(config, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["client_id"] = clientId, ["code_verifier"] = codeVerifier,
        }, clientId, clientSecret, ct);

    /// <summary>Client-credentials token for backend services (client_secret_basic).</summary>
    public Task<SmartToken> ClientCredentialsAsync(SmartConfiguration config, string clientId, string clientSecret, string scopes, CancellationToken ct = default) =>
        TokenAsync(config, new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = scopes }, clientId, clientSecret, ct);

    private async Task<SmartToken> TokenAsync(SmartConfiguration config, Dictionary<string, string> form, string clientId, string? secret, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, config.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        if (secret is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Token request failed ({(int)res.StatusCode}): {body}", null, res.StatusCode);
        return JsonSerializer.Deserialize<SmartToken>(body, Json) ?? throw new InvalidDataException("Empty token response.");
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Adds <c>Authorization: Bearer …</c> to every request (use with Firely's FhirClient or plain HttpClient).</summary>
public sealed class BearerTokenHandler(Func<CancellationToken, ValueTask<string>> token, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct).ConfigureAwait(false));
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
