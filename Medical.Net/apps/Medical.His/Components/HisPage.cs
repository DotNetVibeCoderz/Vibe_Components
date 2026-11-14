using System.Globalization;
using Medical.Hospital;
using Microsoft.AspNetCore.Components;

namespace Medical.His.Components;

/// <summary>Base of every tenant page: loads the session, checks the URL tenant and the role, and re-renders on language change.</summary>
public abstract class HisPage : ComponentBase, IDisposable
{
    [Inject] protected HisSession S { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Parameter] public string Tenant { get; set; } = "";
    [SupplyParameterFromQuery(Name = "lang")] public string? LangQuery { get; set; }

    protected abstract string Module { get; }
    protected bool Ready { get; private set; }
    protected string? Error { get; set; }
    protected string T(string en, string id) => S.T(en, id);
    protected DateTime Now => S.Clock.GetUtcNow().UtcDateTime;
    protected static string Rp(decimal v) => "Rp " + v.ToString("#,0", CultureInfo.GetCultureInfo("id-ID"));
    protected static string Local(DateTime utc) => utc.AddHours(7).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    protected override async Task OnInitializedAsync()
    {
        await S.LoadAsync();
        if (LangQuery is "en" or "id") S.Lang = LangQuery;
        S.Changed += Refresh;
        if (!S.SignedIn || S.TenantId != Tenant) { Nav.NavigateTo("/login", forceLoad: true); return; }
        if (!S.Can(Module)) { Error = T("Your role cannot open this module.", "Peran Anda tidak dapat membuka modul ini."); return; }
        await LoadAsync();
        Ready = true;
    }

    protected abstract Task LoadAsync();

    /// <summary>Runs an action, shows business-rule errors instead of crashing the circuit, then reloads.</summary>
    protected async Task Run(Func<Task> action, string? done = null)
    {
        Error = null; Info = null;
        try { await action(); Info = done; await LoadAsync(); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException) { Error = e.Message; }
    }

    protected string? Info { get; set; }

    private void Refresh() => InvokeAsync(StateHasChanged);
    public virtual void Dispose() { S.Changed -= Refresh; GC.SuppressFinalize(this); }
}
