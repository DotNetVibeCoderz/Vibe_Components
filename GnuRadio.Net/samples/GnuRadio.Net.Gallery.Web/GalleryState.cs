using GnuRadio.Gallery;

namespace GnuRadio.Gallery.Web;

/// <summary>Per-visitor UI state: the chosen language.</summary>
public sealed class GalleryState
{
    public Language Language { get; private set; } = Language.En;

    public event Action? Changed;

    public void SetLanguage(Language language)
    {
        if (Language == language) return;
        Language = language;
        Changed?.Invoke();
    }

    public string T(Text text) => text[Language];

    public string T(string en, string id) => Language == Language.Id ? id : en;
}
