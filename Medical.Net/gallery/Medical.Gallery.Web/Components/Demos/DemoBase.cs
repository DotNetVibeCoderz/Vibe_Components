using Microsoft.AspNetCore.Components;

namespace Medical.Gallery.Web.Components.Demos;

/// <summary>Base for demo views: language-aware and re-rendered when the language changes.</summary>
public abstract class DemoBase : ComponentBase, IDisposable
{
    [Inject] protected UiState Ui { get; set; } = default!;

    protected string Lang => Ui.Lang;
    protected string T(string en, string id) => Ui.T(en, id);
    protected static MarkupString Svg(string svg) => new(svg);
    protected static string DataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
    protected static string Sev(Rules.AlertSeverity s) => s.ToString().ToLowerInvariant();

    protected override void OnInitialized() => Ui.Changed += Changed;
    private void Changed() => InvokeAsync(StateHasChanged);
    public void Dispose()
    {
        Ui.Changed -= Changed;
        GC.SuppressFinalize(this);
    }
}
