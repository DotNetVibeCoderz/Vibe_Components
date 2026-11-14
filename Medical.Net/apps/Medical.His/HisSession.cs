using System.Security.Claims;
using Medical.Hospital;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Medical.His;

/// <summary>
/// The signed-in user of a Blazor circuit: tenant, role and language from the cookie claims. Pages open a short-lived
/// <see cref="HospitalDbContext"/> per operation through <see cref="Db"/>, which is filtered to the user's tenant.
/// </summary>
public sealed class HisSession(DbContextOptions<HospitalDbContext> options, AuthenticationStateProvider auth, TimeProvider clock) : ITenantContext
{
    public string TenantId { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public StaffRole Role { get; private set; }
    public string StaffId { get; private set; } = "";
    public string Lang { get; set; } = "id";
    public string TenantName { get; private set; } = "";
    public bool SignedIn => TenantId.Length > 0;
    public TimeProvider Clock => clock;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        if (SignedIn) return;
        var user = (await auth.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true) return;
        TenantId = user.FindFirst("tenant")?.Value ?? "";
        UserName = user.Identity.Name ?? "";
        DisplayName = user.FindFirst("display")?.Value ?? UserName;
        StaffId = user.FindFirst("staff")?.Value ?? "";
        Role = Enum.TryParse<StaffRole>(user.FindFirst(ClaimTypes.Role)?.Value, out var r) ? r : StaffRole.Registration;
        Lang = user.FindFirst("lang")?.Value ?? "id";
        await using var db = Db();
        TenantName = (await db.Tenants.FirstOrDefaultAsync(t => t.Id == TenantId))?.Name ?? TenantId;
    }

    public HospitalDbContext Db() => new(options, this);

    public string T(string en, string id) => Lang == "en" ? en : id;

    public void SetLang(string lang) { Lang = lang; Changed?.Invoke(); }

    /// <summary>Which modules a role may use (menu and page guard).</summary>
    public bool Can(string module) => Role == StaffRole.Admin || module switch
    {
        "dashboard" => true,
        "registration" => Role is StaffRole.Registration or StaffRole.Doctor or StaffRole.Nurse,
        "outpatient" => Role is StaffRole.Doctor or StaffRole.Nurse,
        "wards" => Role is StaffRole.Doctor or StaffRole.Nurse,
        "monitoring" => Role is StaffRole.Doctor or StaffRole.Nurse,
        "pharmacy" => Role is StaffRole.Pharmacist or StaffRole.Doctor,
        "lab" => Role is StaffRole.LabAnalyst or StaffRole.Doctor,
        "radiology" => Role is StaffRole.Radiographer or StaffRole.Radiologist or StaffRole.Doctor,
        "billing" => Role is StaffRole.Cashier or StaffRole.Coder,
        _ => false,
    };
}
