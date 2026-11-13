using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Medical.Core;
using Medical.Fhir.Search;
using Medical.Fhir.Smart;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Medical.AspNetCore.Smart;

/// <summary>A registered SMART app.</summary>
public sealed record SmartClientRegistration(string ClientId, IReadOnlyList<string> RedirectUris, string AllowedScopes, string? Secret = null)
{
    public bool Confidential => Secret is not null;
}

/// <summary>Options for the built-in SMART on FHIR authorization server.</summary>
public sealed class SmartServerOptions
{
    /// <summary>When true, every FHIR request needs a valid bearer token with matching scopes.</summary>
    public bool RequireAuthorization { get; set; } = true;
    /// <summary>HMAC signing key (≥ 32 bytes). A random key is generated per process when empty — set it to keep tokens valid across restarts.</summary>
    public string SigningKey { get; set; } = "";
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public List<SmartClientRegistration> Clients { get; } = [];
    /// <summary>Patient selected for standalone launches with <c>launch/patient</c> when the request does not name one (development).</summary>
    public string? DefaultPatient { get; set; }
    /// <summary>
    /// Development mode: the authorize endpoint approves immediately (no login page). Put a real identity provider in front
    /// of <c>/auth/authorize</c> for production, or replace this server with your organisation's SMART authorization server.
    /// </summary>
    public bool AutoApprove { get; set; } = true;
}

/// <summary>The caller's SMART context, available to FHIR handlers via <see cref="HttpContext.Items"/>.</summary>
public sealed record SmartPrincipal(string ClientId, IReadOnlyList<SmartScope> Scopes, string? Patient, string? Encounter)
{
    public const string ItemKey = "medical.smart";

    /// <summary>True when the only grant is patient-compartment access.</summary>
    public bool PatientBound => Patient is not null && Scopes.All(s => s.Compartment == SmartCompartment.Patient);

    public bool Allows(string type, char permission) => Scopes.Any(s => s.Allows(type, permission));

    /// <summary>For patient-bound tokens: is the resource the patient or in their compartment?</summary>
    public bool CanSee(global::Hl7.Fhir.Model.Resource r) =>
        !PatientBound || (r.TypeName == "Patient" && r.Id == Patient) ||
        FhirSearchIndex.Extract(r).Any(e => e.Param is "patient" && e.Value == $"Patient/{Patient}");
}

/// <summary>Issued codes and launch contexts (in-memory, short-lived).</summary>
public sealed class SmartAuthorizationStore
{
    internal sealed record PendingCode(string ClientId, string RedirectUri, string Scope, string Challenge, string? Patient, string? Encounter, DateTimeOffset Expires);
    internal sealed record LaunchContext(string? Patient, string? Encounter, DateTimeOffset Expires);

    internal ConcurrentDictionary<string, PendingCode> Codes { get; } = new();
    internal ConcurrentDictionary<string, LaunchContext> Launches { get; } = new();
}

public static class SmartServerExtensions
{
    /// <summary>Registers the SMART authorization server; call <see cref="MapSmartAuthorization"/> and pass the same options.</summary>
    public static MedicalBuilder AddSmartOnFhir(this MedicalBuilder builder, Action<SmartServerOptions> configure) =>
        builder.TryAddModule("smart", s =>
        {
            var o = new SmartServerOptions();
            configure(o);
            if (o.SigningKey.Length == 0) o.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            if (Encoding.UTF8.GetByteCount(o.SigningKey) < 32) throw new ArgumentException("SMART signing key must be at least 32 bytes.");
            s.AddSingleton(o);
            s.AddSingleton<SmartAuthorizationStore>();
        });

    private static SymmetricSecurityKey Key(SmartServerOptions o) => new(Encoding.UTF8.GetBytes(o.SigningKey));

    /// <summary>Maps <c>{fhir}/.well-known/smart-configuration</c>, <c>{auth}/authorize</c>, <c>{auth}/token</c> and <c>{auth}/launch</c>.</summary>
    public static IEndpointRouteBuilder MapSmartAuthorization(this IEndpointRouteBuilder app, string fhirPrefix = "/fhir", string authPrefix = "/auth")
    {
        app.MapGet($"{fhirPrefix}/.well-known/smart-configuration", (HttpContext ctx) =>
        {
            string root = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(new SmartConfiguration
            {
                Issuer = root + authPrefix,
                AuthorizationEndpoint = root + authPrefix + "/authorize",
                TokenEndpoint = root + authPrefix + "/token",
                GrantTypesSupported = ["authorization_code", "client_credentials"],
                ResponseTypesSupported = ["code"],
                CodeChallengeMethodsSupported = ["S256"],
                TokenEndpointAuthMethodsSupported = ["client_secret_basic", "none"],
                ScopesSupported = ["launch", "launch/patient", "openid", "fhirUser", "offline_access", "patient/*.rs", "user/*.cruds", "system/*.rs", "patient/*.read", "user/*.*"],
                Capabilities = ["launch-ehr", "launch-standalone", "client-public", "client-confidential-symmetric", "context-standalone-patient",
                                "context-ehr-patient", "permission-patient", "permission-user", "permission-v1", "permission-v2"],
            }, contentType: "application/json");
        }).AllowAnonymous();

        // EHR launch: the EHR registers the context and passes the opaque launch id to the app.
        app.MapPost($"{authPrefix}/launch", async (HttpContext ctx, SmartAuthorizationStore store) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            string launch = SmartClient.Base64Url(RandomNumberGenerator.GetBytes(16));
            store.Launches[launch] = new(form["patient"].FirstOrDefault(), form["encounter"].FirstOrDefault(), DateTimeOffset.UtcNow.AddMinutes(10));
            return Results.Json(new { launch });
        });

        app.MapGet($"{authPrefix}/authorize", (HttpContext ctx, SmartServerOptions o, SmartAuthorizationStore store) =>
        {
            var q = ctx.Request.Query;
            string? clientId = q["client_id"], redirect = q["redirect_uri"], scope = q["scope"], state = q["state"];
            var client = o.Clients.FirstOrDefault(c => c.ClientId == clientId);
            if (client is null || redirect is null || !client.RedirectUris.Contains(redirect))
                return Results.BadRequest(new { error = "invalid_client", error_description = "Unknown client or redirect_uri." });
            string Fail(string error) => $"{redirect}{(redirect.Contains('?') ? '&' : '?')}error={error}&state={Uri.EscapeDataString(state ?? "")}";
            if (q["response_type"] != "code") return Results.Redirect(Fail("unsupported_response_type"));
            if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"])) return Results.Redirect(Fail("invalid_request"));
            if (!o.AutoApprove) return Results.Redirect(Fail("access_denied"));
            string granted = Grant(scope ?? "", client.AllowedScopes);
            string? patient = null, encounter = null;
            if (q["launch"].FirstOrDefault() is { } launch)
            {
                if (!store.Launches.TryRemove(launch, out var lc) || lc.Expires < DateTimeOffset.UtcNow) return Results.Redirect(Fail("invalid_request"));
                (patient, encounter) = (lc.Patient, lc.Encounter);
            }
            else if (granted.Split(' ').Contains("launch/patient")) patient = q["patient"].FirstOrDefault() ?? o.DefaultPatient;
            string code = SmartClient.Base64Url(RandomNumberGenerator.GetBytes(32));
            store.Codes[code] = new(client.ClientId, redirect, granted, q["code_challenge"]!, patient, encounter, DateTimeOffset.UtcNow.AddMinutes(5));
            return Results.Redirect($"{redirect}{(redirect.Contains('?') ? '&' : '?')}code={code}&state={Uri.EscapeDataString(state ?? "")}");
        });

        app.MapPost($"{authPrefix}/token", async (HttpContext ctx, SmartServerOptions o, SmartAuthorizationStore store) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var (basicId, basicSecret) = BasicAuth(ctx.Request);
            string? clientId = basicId ?? form["client_id"];
            var client = o.Clients.FirstOrDefault(c => c.ClientId == clientId);
            if (client is null || (client.Confidential && !FixedEquals(client.Secret!, basicSecret ?? form["client_secret"].FirstOrDefault() ?? "")))
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);

            string fhirBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}{fhirPrefix}";
            switch (form["grant_type"].FirstOrDefault())
            {
                case "authorization_code":
                    if (!store.Codes.TryRemove(form["code"].FirstOrDefault() ?? "", out var pc) || pc.Expires < DateTimeOffset.UtcNow || pc.ClientId != client.ClientId)
                        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                    if (pc.RedirectUri != form["redirect_uri"].FirstOrDefault()) return Results.Json(new { error = "invalid_grant", error_description = "redirect_uri mismatch" }, statusCode: 400);
                    string verifier = form["code_verifier"].FirstOrDefault() ?? "";
                    if (!FixedEquals(pc.Challenge, SmartClient.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))))
                        return Results.Json(new { error = "invalid_grant", error_description = "PKCE verification failed" }, statusCode: 400);
                    return Results.Json(Issue(o, client.ClientId, pc.Scope, pc.Patient, pc.Encounter, fhirBase));
                case "client_credentials":
                    if (!client.Confidential) return Results.Json(new { error = "unauthorized_client" }, statusCode: 400);
                    string scope = Grant(form["scope"].FirstOrDefault() ?? "", client.AllowedScopes);
                    if (SmartScope.ParseAll(scope).Any(s => s.Compartment != SmartCompartment.System))
                        return Results.Json(new { error = "invalid_scope", error_description = "Backend services may only request system/ scopes." }, statusCode: 400);
                    return Results.Json(Issue(o, client.ClientId, scope, null, null, fhirBase));
                default:
                    return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
            }
        }).DisableAntiforgery();
        return app;
    }

    /// <summary>Requested scopes intersected with what the client may receive (wildcards honoured).</summary>
    private static string Grant(string requested, string allowed)
    {
        var allowedList = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowedScopes = SmartScope.ParseAll(allowed);
        return string.Join(' ', requested.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(r =>
            allowedList.Contains(r) || (SmartScope.Parse(r) is { } rs && allowedScopes.Any(a => a.Compartment == rs.Compartment &&
                (a.ResourceType == "*" || a.ResourceType == rs.ResourceType) && rs.Permissions.All(a.Permissions.Contains)))));
    }

    private static SmartToken Issue(SmartServerOptions o, string clientId, string scope, string? patient, string? encounter, string audience)
    {
        var claims = new Dictionary<string, object> { ["client_id"] = clientId, ["scope"] = scope };
        if (patient is not null) claims["patient"] = patient;
        if (encounter is not null) claims["encounter"] = encounter;
        string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "medical-net-smart", Audience = audience, Subject = new ClaimsIdentity([new Claim("sub", clientId)]), Claims = claims,
            Expires = DateTime.UtcNow.Add(o.TokenLifetime), SigningCredentials = new SigningCredentials(Key(o), SecurityAlgorithms.HmacSha256),
        });
        return new SmartToken { AccessToken = token, ExpiresIn = (int)o.TokenLifetime.TotalSeconds, Scope = scope, Patient = patient, Encounter = encounter };
    }

    /// <summary>Validates the bearer token of a FHIR request; null when missing or invalid.</summary>
    public static async Task<SmartPrincipal?> AuthenticateAsync(HttpContext ctx, SmartServerOptions o)
    {
        string? header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(header[7..].Trim(), new TokenValidationParameters
        {
            ValidIssuer = "medical-net-smart", ValidateAudience = false, IssuerSigningKey = Key(o), ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!result.IsValid) return null;
        string Claim(string n) => result.Claims.TryGetValue(n, out var v) ? v?.ToString() ?? "" : "";
        return new SmartPrincipal(Claim("client_id"), SmartScope.ParseAll(Claim("scope")), NullIfEmpty(Claim("patient")), NullIfEmpty(Claim("encounter")));
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static (string? Id, string? Secret) BasicAuth(HttpRequest r)
    {
        string? h = r.Headers.Authorization.FirstOrDefault();
        if (h is null || !h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return (null, null);
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..].Trim())).Split(':', 2);
            return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null);
        }
        catch (FormatException) { return (null, null); }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
