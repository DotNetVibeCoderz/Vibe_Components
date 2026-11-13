namespace Medical.Core;

/// <summary>A bilingual (English / Bahasa Indonesia) string.</summary>
public readonly record struct Localized(string En, string Id)
{
    public string this[string lang] => lang == "id" ? Id : En;
    public string Get(string lang) => lang == "id" ? Id : En;
    public override string ToString() => En;
}
