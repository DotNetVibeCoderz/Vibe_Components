using System.Security.Claims;
using Medical.Hospital;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;

namespace Medical.His;

/// <summary>
/// Sign-in for the HIS. The identity provider (OpenID Connect: Keycloak, Microsoft Entra ID, …) proves who the user
/// is; the HIS staff table decides what they may do: a user is admitted to a hospital only when that tenant appears in
/// the IdP's tenant claim <i>and</i> an active staff record with the same user name exists there, and the role comes
/// from that record. The demo login (no password) uses the same principal builder.
/// </summary>
public static class HisAuth
{
    public const string OidcScheme = "oidc";

    public static bool OidcConfigured(IConfiguration c) => !string.IsNullOrWhiteSpace(c["His:Oidc:Authority"]);

    /// <summary>The cookie principal for a staff member of a tenant, or null when the user or tenant is unknown.</summary>
    public static async Task<ClaimsPrincipal?> PrincipalAsync(DbContextOptions<HospitalDbContext> opts, string tenant, string user, string? lang, string method)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(user)) return null;
        await using var db = new HospitalDbContext(opts, new FixedTenantContext(tenant, user));
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.UserName == user);
        if (staff is null || !await db.Tenants.AnyAsync(t => t.Id == tenant && t.Active)) return null;
        db.AuditEvent(Medical.Core.AuditAction.Login, "Session", null, $"{staff.UserName} ({method})");
        await db.SaveChangesAsync();
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, staff.UserName), new Claim("display", staff.Name), new Claim(ClaimTypes.Role, staff.Role.ToString()),
            new Claim("tenant", tenant), new Claim("lang", lang is "en" ? "en" : "id"), new Claim("staff", staff.Id), new Claim("amr", method),
        ], CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>Local paths only: "//host" and "/\host" would be protocol-relative redirects to another site.</summary>
    public static string LocalOr(string? returnUrl, string fallback) =>
        returnUrl is { Length: > 1 } r && r[0] == '/' && r[1] != '/' && r[1] != '\\' ? r : fallback;

    /// <summary>
    /// Adds OpenID Connect (authorization code + PKCE) when <c>His:Oidc:Authority</c> is set. Settings:
    /// <c>Authority, ClientId, ClientSecret, TenantClaim</c> (default "tenant", may be multi-valued),
    /// <c>UserClaim</c> (default "preferred_username"), <c>RequireHttpsMetadata</c> (default true).
    /// </summary>
    public static AuthenticationBuilder AddHisOidc(this AuthenticationBuilder auth, IConfiguration c)
    {
        if (!OidcConfigured(c)) return auth;
        string tenantClaim = c["His:Oidc:TenantClaim"] ?? "tenant", userClaim = c["His:Oidc:UserClaim"] ?? "preferred_username";
        return auth.AddOpenIdConnect(OidcScheme, "SSO", o =>
        {
            o.Authority = c["His:Oidc:Authority"];
            o.ClientId = c["His:Oidc:ClientId"];
            o.ClientSecret = c["His:Oidc:ClientSecret"];
            o.ResponseType = "code";
            o.UsePkce = true;
            o.RequireHttpsMetadata = c.GetValue("His:Oidc:RequireHttpsMetadata", true);
            o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.SaveTokens = false;
            o.Scope.Clear();
            foreach (var s in (c["His:Oidc:Scopes"] ?? "openid profile").Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Scope.Add(s);
            o.Events.OnTokenValidated = async ctx =>
            {
                var external = ctx.Principal!;
                string tenant = ctx.Properties?.Items.TryGetValue("tenant", out var t) == true ? t ?? "" : "";
                var allowed = external.FindAll(tenantClaim).SelectMany(x => x.Value.Split(' ', ',')).ToHashSet(StringComparer.Ordinal);
                if (tenant.Length == 0 && allowed.Count == 1) tenant = allowed.First();
                string? user = external.FindFirst(userClaim)?.Value;
                var opts = ctx.HttpContext.RequestServices.GetRequiredService<DbContextOptions<HospitalDbContext>>();
                var principal = allowed.Contains(tenant) && user is not null ? await PrincipalAsync(opts, tenant, user, (ctx.Properties?.Items.TryGetValue("lang", out var lg) == true ? lg : null), "oidc") : null;
                if (principal is null) { ctx.Fail($"'{user}' is not an active staff member of '{tenant}'."); return; }
                ctx.Principal = principal;
            };
            o.Events.OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/login?error=sso");
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
        });
    }
}
