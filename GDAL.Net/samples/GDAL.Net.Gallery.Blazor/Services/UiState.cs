namespace GDAL.Net.Gallery.Blazor.Services;

/// <summary>Per-circuit UI preferences (language, theme).</summary>
public sealed class UiState
{
    public Lang Lang { get; private set; } = Lang.En;

    public bool Dark { get; private set; }

    public event Action? Changed;

    public string T(Text text) => text[Lang];

    public string T(string en, string id) => Lang == Lang.Id ? id : en;

    public void SetLang(Lang lang)
    {
        if (Lang == lang) return;
        Lang = lang;
        Changed?.Invoke();
    }

    public void SetDark(bool dark)
    {
        if (Dark == dark) return;
        Dark = dark;
        Changed?.Invoke();
    }
}
