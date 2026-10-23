using CesiumNet.Blazor;
using Microsoft.JSInterop;

namespace CesiumNet.Gallery.Blazor;

/// <summary>Per-circuit gallery preferences (language, ion token), persisted in the browser.</summary>
public sealed class GalleryState(IJSRuntime js, CesiumNetBlazorOptions defaults)
{
    private bool _loaded;

    public GalleryLanguage Language { get; private set; } = GalleryLanguage.En;

    /// <summary>Token from browser storage, falling back to server configuration.</summary>
    public string? IonToken { get; private set; } = defaults.IonAccessToken;

    public event Action? Changed;

    public string T(Text text) => text[Language];

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var lang = await js.InvokeAsync<string?>("galleryPrefs.get", "lang");
            if (lang == "id") Language = GalleryLanguage.Id;
            var token = await js.InvokeAsync<string?>("galleryPrefs.get", "ionToken");
            if (!string.IsNullOrWhiteSpace(token)) IonToken = token;
            Changed?.Invoke();
        }
        catch (JSException) { /* storage unavailable: keep defaults */ }
    }

    public async Task SetLanguageAsync(GalleryLanguage language)
    {
        Language = language;
        Changed?.Invoke();
        try { await js.InvokeVoidAsync("galleryPrefs.set", "lang", language == GalleryLanguage.Id ? "id" : "en"); }
        catch (JSException) { }
    }

    public async Task SetIonTokenAsync(string? token)
    {
        IonToken = string.IsNullOrWhiteSpace(token) ? defaults.IonAccessToken : token.Trim();
        Changed?.Invoke();
        try { await js.InvokeVoidAsync("galleryPrefs.set", "ionToken", token?.Trim() ?? ""); }
        catch (JSException) { }
    }
}
