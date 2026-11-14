using Avalonia.Media;

namespace Electrical.Gallery.Desktop;

/// <summary>
/// "Control-room mimic" palette shared with the Blazor gallery (app.css). <see cref="Dark"/> selects the variant; the window rebuilds its
/// content when the theme changes, so every brush is read through these properties.
/// </summary>
internal static class Pal
{
    public static bool Dark { get; set; } = true;

    private static SolidColorBrush B(string light, string dark) => new(Color.Parse(Dark ? dark : light));

    public static IBrush Bg => B("#EEF2F7", "#0B1630");
    public static IBrush BgSunk => B("#E3E9F1", "#08112A");
    public static IBrush Panel => B("#FFFFFF", "#101F3D");
    public static IBrush Panel2 => B("#F6F8FB", "#0D1A35");
    public static IBrush Ink => B("#0B1630", "#E6EDF7");
    public static IBrush Ink2 => B("#3C4D69", "#B3C2D9");
    public static IBrush Ink3 => B("#6A7A93", "#7F92B0");
    public static IBrush Rule => B("#D5DDE8", "#1D3157");
    public static IBrush Rule2 => B("#BCC8D8", "#2A4373");
    public static IBrush Cyan => B("#0091AD", "#22D3EE");
    public static IBrush Amber => B("#C97F00", "#F5B342");
    public static IBrush Red => B("#D62F3A", "#FF5C63");
    public static IBrush Green => B("#1F9D5F", "#2BB673");
    public static IBrush LampOff => B("#C5CFDC", "#23385F");
    public static IBrush CodeBg => new SolidColorBrush(Color.Parse("#07102A"));
    public static IBrush CodeInk => new SolidColorBrush(Color.Parse("#DBE6F5"));

    public static IBrush Glow(string tone) => tone switch
    {
        "alarm" => B("#1FD62F3A", "#29FF5C63"),
        "warn" => B("#24D98A00", "#24F5B342"),
        "ok" => B("#1A1F9D5F", "#1F2BB673"),
        _ => B("#1A0091AD", "#2422D3EE"),
    };

    public static IBrush Tone(string? tone) => tone switch
    {
        "ok" => Green, "warn" => Amber, "alarm" => Red, _ => LampOff,
    };

    public static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));
}
