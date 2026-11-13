namespace Medical.Gallery.Web;

/// <summary>Per-circuit UI state (language).</summary>
public sealed class UiState
{
    public string Lang { get; private set; } = "en";
    public event Action? Changed;

    public void SetLang(string lang)
    {
        if (lang is not ("en" or "id") || lang == Lang) return;
        Lang = lang;
        Changed?.Invoke();
    }

    public string T(string en, string id) => Lang == "id" ? id : en;
}
