using System.Globalization;

namespace Medical.Core;

/// <summary>A measured value with a UCUM unit (e.g. <c>72 /min</c>, <c>37.2 Cel</c>, <c>120 mm[Hg]</c>).</summary>
public readonly record struct Quantity(double Value, string Unit)
{
    /// <summary>Converts to another UCUM unit of the same dimension.</summary>
    public Quantity To(string unit) => new(Ucum.Convert(Value, Unit, unit), unit);

    public override string ToString() => $"{Value.ToString("0.##", CultureInfo.InvariantCulture)} {Ucum.Display(Unit)}";
}

/// <summary>
/// Minimal UCUM support for the units that appear in vital signs and common labs. Analyte-specific conversions
/// (e.g. glucose mg/dL ↔ mmol/L) take the molar mass explicitly.
/// </summary>
public static class Ucum
{
    private static readonly Dictionary<string, (string Dim, double Factor)> Linear = new(StringComparer.Ordinal)
    {
        // mass → g
        ["kg"] = ("mass", 1000), ["g"] = ("mass", 1), ["mg"] = ("mass", 1e-3), ["ug"] = ("mass", 1e-6), ["[lb_av]"] = ("mass", 453.59237), ["[oz_av]"] = ("mass", 28.349523125),
        // length → m
        ["m"] = ("length", 1), ["cm"] = ("length", 0.01), ["mm"] = ("length", 0.001), ["[in_i]"] = ("length", 0.0254), ["[ft_i]"] = ("length", 0.3048),
        // pressure → Pa
        ["mm[Hg]"] = ("pressure", 133.322387415), ["kPa"] = ("pressure", 1000), ["Pa"] = ("pressure", 1), ["cm[H2O]"] = ("pressure", 98.0665),
        // volume → L
        ["L"] = ("volume", 1), ["dL"] = ("volume", 0.1), ["mL"] = ("volume", 1e-3),
        // time → s
        ["s"] = ("time", 1), ["min"] = ("time", 60), ["h"] = ("time", 3600), ["d"] = ("time", 86400), ["wk"] = ("time", 604800), ["a"] = ("time", 31557600),
        // concentration (mass) → g/L
        ["g/L"] = ("massconc", 1), ["g/dL"] = ("massconc", 10), ["mg/dL"] = ("massconc", 0.01), ["mg/L"] = ("massconc", 1e-3),
        // concentration (substance) → mol/L
        ["mol/L"] = ("molconc", 1), ["mmol/L"] = ("molconc", 1e-3), ["umol/L"] = ("molconc", 1e-6),
        // rate → per minute
        ["/min"] = ("rate", 1), ["{beats}/min"] = ("rate", 1), ["{breaths}/min"] = ("rate", 1), ["/s"] = ("rate", 60), ["/h"] = ("rate", 1.0 / 60),
        ["%"] = ("ratio", 0.01), ["1"] = ("ratio", 1),
    };

    public static bool IsKnown(string unit) => Linear.ContainsKey(unit) || unit is "Cel" or "[degF]" or "K";

    /// <summary>Converts <paramref name="value"/> between UCUM units of the same dimension.</summary>
    public static double Convert(double value, string from, string to)
    {
        if (from == to) return value;
        if (IsTemperature(from) && IsTemperature(to)) return FromKelvin(ToKelvin(value, from), to);
        if (!Linear.TryGetValue(from, out var f) || !Linear.TryGetValue(to, out var t))
            throw new NotSupportedException($"Unsupported UCUM unit conversion '{from}' → '{to}'.");
        if (f.Dim != t.Dim) throw new InvalidOperationException($"Incompatible units '{from}' ({f.Dim}) and '{to}' ({t.Dim}).");
        return value * f.Factor / t.Factor;
    }

    /// <summary>mg/dL → mmol/L for an analyte with molar mass <paramref name="gramsPerMole"/> (glucose 180.16, cholesterol 386.65, creatinine 113.12 → µmol/L ×1000).</summary>
    public static double MgPerDlToMmolPerL(double mgPerDl, double gramsPerMole) => mgPerDl * 10.0 / gramsPerMole;

    public static double MmolPerLToMgPerDl(double mmolPerL, double gramsPerMole) => mmolPerL * gramsPerMole / 10.0;

    /// <summary>Human-friendly rendering of a UCUM code.</summary>
    public static string Display(string unit) => unit switch
    {
        "Cel" => "°C", "[degF]" => "°F", "mm[Hg]" => "mmHg", "/min" => "/min", "{beats}/min" => "bpm", "{breaths}/min" => "/min",
        "[lb_av]" => "lb", "[in_i]" => "in", "[ft_i]" => "ft", "ug" => "µg", "umol/L" => "µmol/L", "kg/m2" => "kg/m²", "a" => "y",
        "10*3/uL" => "×10³/µL", "10*9/L" => "×10⁹/L",
        _ => unit,
    };

    private static bool IsTemperature(string u) => u is "Cel" or "[degF]" or "K";

    private static double ToKelvin(double v, string u) => u switch { "Cel" => v + 273.15, "[degF]" => (v - 32) * 5 / 9 + 273.15, _ => v };

    private static double FromKelvin(double k, string u) => u switch { "Cel" => k - 273.15, "[degF]" => (k - 273.15) * 9 / 5 + 32, _ => k };
}
