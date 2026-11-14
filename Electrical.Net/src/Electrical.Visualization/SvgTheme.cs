using System.Text;
using System.Text.RegularExpressions;

namespace Electrical.Visualization;

/// <summary>One themable CSS custom property used by all Electrical.Net SVG output.</summary>
/// <param name="Name">Variable name without the leading <c>--</c> (e.g. <c>eln-fg</c>).</param>
/// <param name="Light">Literal color for the light theme.</param>
/// <param name="Dark">Literal color for the dark theme.</param>
/// <param name="Description">What the variable is used for.</param>
public sealed record SvgThemeVariable(string Name, string Light, string Dark, string Description);

/// <summary>
/// Electrical.Net "Grid Control Room" palette. Every SVG produced by this library refers to colors through CSS custom properties
/// (<c>var(--eln-fg, #0f1e33)</c>) so web hosts can theme it; <see cref="Resolve"/> substitutes literal colors for renderers that do
/// not understand CSS variables (Svg.Skia / Avalonia, PNG rasterizers).
/// </summary>
/// <remarks>
/// Brand: deep navy surfaces, electric-cyan accent, amber highlights, signal red/green for limits.
/// <list type="table">
/// <item><term>--eln-bg</term><description>page / chart background</description></item>
/// <item><term>--eln-surface</term><description>cards, legends, symbol fills</description></item>
/// <item><term>--eln-fg</term><description>primary ink: text, bus bars, symbols</description></item>
/// <item><term>--eln-muted</term><description>axis labels, secondary text</description></item>
/// <item><term>--eln-grid</term><description>grid lines, gauge tracks</description></item>
/// <item><term>--eln-accent</term><description>electric cyan: primary series, highlights</description></item>
/// <item><term>--eln-amber</term><description>secondary accent, warnings</description></item>
/// <item><term>--eln-danger</term><description>violations, trips, overloads</description></item>
/// <item><term>--eln-ok</term><description>normal / healthy state</description></item>
/// <item><term>--eln-line</term><description>de-energized or unrated branches</description></item>
/// <item><term>--eln-v-low, --eln-v-lowish</term><description>under-voltage bands (GIS maps)</description></item>
/// <item><term>--eln-series-1 … --eln-series-8</term><description>categorical series colors</description></item>
/// <item><term>--eln-font</term><description>font family</description></item>
/// </list>
/// </remarks>
public static partial class SvgTheme
{
    /// <summary>Default font stack used by the SVG output.</summary>
    public const string FontStack = "'Inter', 'Segoe UI', system-ui, sans-serif";

    /// <summary>All theme variables with their light and dark literal values.</summary>
    public static IReadOnlyList<SvgThemeVariable> Variables { get; } =
    [
        new("eln-bg", "#f7f9fc", "#0b1626", "Page / chart background"),
        new("eln-surface", "#ffffff", "#12223a", "Cards, legends and symbol fills"),
        new("eln-fg", "#0f1e33", "#e6edf7", "Primary ink: text, bus bars, symbols"),
        new("eln-muted", "#5b6b82", "#8fa3bf", "Axis labels and secondary text"),
        new("eln-grid", "#dfe6ef", "#1f334f", "Grid lines and gauge tracks"),
        new("eln-accent", "#0091ad", "#22d3ee", "Electric cyan: primary series and highlights"),
        new("eln-amber", "#d98a00", "#f5a524", "Secondary accent and warnings"),
        new("eln-danger", "#d62f3a", "#ff5c63", "Violations, trips and overloads"),
        new("eln-ok", "#1f9d5f", "#2bb673", "Normal / healthy state"),
        new("eln-line", "#8b99ad", "#5d7090", "De-energized or unrated branches"),
        new("eln-v-low", "#2f6bff", "#5b8cff", "Under-voltage band (< 0.95 pu), used by GIS maps"),
        new("eln-v-lowish", "#6fa8ff", "#8fbcff", "Slightly low voltage band (0.95-0.98 pu), used by GIS maps"),
        new("eln-series-1", "#0091ad", "#22d3ee", "Series 1 (cyan)"),
        new("eln-series-2", "#d98a00", "#f5a524", "Series 2 (amber)"),
        new("eln-series-3", "#6d4bd8", "#a78bfa", "Series 3 (violet)"),
        new("eln-series-4", "#1f9d5f", "#2bb673", "Series 4 (green)"),
        new("eln-series-5", "#d62f3a", "#ff5c63", "Series 5 (signal red)"),
        new("eln-series-6", "#2f5fc4", "#60a5fa", "Series 6 (grid blue)"),
        new("eln-series-7", "#b5359f", "#e879f9", "Series 7 (magenta)"),
        new("eln-series-8", "#56657a", "#94a3b8", "Series 8 (slate)"),
        new("eln-font", FontStack, FontStack, "Font family"),
    ];

    private static readonly Dictionary<string, SvgThemeVariable> ByName = Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);

    /// <summary>Returns <c>var(--name, lightLiteral)</c> for a known theme variable.</summary>
    public static string Var(string name) =>
        ByName.TryGetValue(name, out var v) ? $"var(--{name}, {v.Light})" : throw new ArgumentException($"Unknown theme variable '{name}'.", nameof(name));

    /// <summary>Categorical series color as a CSS variable reference (cycles through 8 colors).</summary>
    public static string Series(int index) => Var($"eln-series-{index % 8 + 1}");

    /// <summary>Literal color of a theme variable.</summary>
    public static string Literal(string name, bool dark) =>
        ByName.TryGetValue(name, out var v) ? (dark ? v.Dark : v.Light) : throw new ArgumentException($"Unknown theme variable '{name}'.", nameof(name));

    [GeneratedRegex(@"var\(\s*--([A-Za-z0-9_-]+)\s*(?:,\s*((?:[^()]|\([^()]*\))*))?\)")]
    private static partial Regex VarRegex();

    /// <summary>
    /// Replaces every <c>var(--x[, fallback])</c> in <paramref name="svg"/> with the literal light or dark value
    /// (unknown variables fall back to their inline fallback) and <c>currentColor</c> with the foreground color.
    /// </summary>
    public static string Resolve(string svg, bool dark)
    {
        ArgumentNullException.ThrowIfNull(svg);
        string r = VarRegex().Replace(svg, m =>
        {
            if (ByName.TryGetValue(m.Groups[1].Value, out var v)) return dark ? v.Dark : v.Light;
            return m.Groups[2].Success ? m.Groups[2].Value.Trim() : "#000000";
        });
        return r.Replace("currentColor", dark ? "#e6edf7" : "#0f1e33", StringComparison.Ordinal);
    }

    /// <summary>CSS declarations (<c>--eln-fg: …;</c>) for one theme, for use inside a selector.</summary>
    public static string CssDeclarations(bool dark)
    {
        var sb = new StringBuilder();
        foreach (var v in Variables) sb.Append("--").Append(v.Name).Append(": ").Append(dark ? v.Dark : v.Light).Append("; ");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Complete stylesheet: light values on <c>:root</c>, dark values under <c>prefers-color-scheme: dark</c> and on
    /// <c>[data-theme="dark"]</c>, light forced by <c>[data-theme="light"]</c>.
    /// </summary>
    public static string Stylesheet =>
        $":root {{ {CssDeclarations(false)} }}\n" +
        $"@media (prefers-color-scheme: dark) {{ :root:not([data-theme=\"light\"]) {{ {CssDeclarations(true)} }} }}\n" +
        $":root[data-theme=\"dark\"] {{ {CssDeclarations(true)} }}\n";
}
