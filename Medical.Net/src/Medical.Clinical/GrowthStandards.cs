using System.Globalization;
using Medical.Core;

namespace Medical.Clinical;

/// <summary>Anthropometric indicators of the WHO Child Growth Standards (0–5 y) and the WHO Growth Reference (5–19 y).</summary>
public enum GrowthIndicator
{
    WeightForAge,
    LengthHeightForAge,
    WeightForLengthHeight,
    BmiForAge,
    HeadCircumferenceForAge,
    ArmCircumferenceForAge,
    SubscapularSkinfoldForAge,
    TricepsSkinfoldForAge,
}

/// <summary>Box-Cox power, median and coefficient of variation at one value of the axis (age in days / months, or length in cm).</summary>
public readonly record struct Lms(double L, double M, double S)
{
    /// <summary>LMS z-score (Cole &amp; Green 1992).</summary>
    public double Z(double y) => Math.Abs(L) < 1e-12 ? Math.Log(y / M) / S : (Math.Pow(y / M, L) - 1) / (S * L);

    /// <summary>The measurement at z-score <paramref name="z"/>.</summary>
    public double ValueAt(double z) => Math.Abs(L) < 1e-12 ? M * Math.Exp(S * z) : M * Math.Pow(1 + L * S * z, 1 / L);

    /// <summary>
    /// WHO restricted z-score for weight-based indicators: beyond ±3 SD the distance is measured in units of the
    /// 2–3 SD interval at that age, because the Box-Cox tail is not reliable there (WHO Child Growth Standards, 2006).
    /// </summary>
    public double ZRestricted(double y)
    {
        double z = Z(y);
        if (z > 3) { double sd3 = ValueAt(3), sd23 = sd3 - ValueAt(2); return 3 + (y - sd3) / sd23; }
        if (z < -3) { double sd3 = ValueAt(-3), sd23 = ValueAt(-2) - sd3; return -3 + (y - sd3) / sd23; }
        return z;
    }
}

/// <summary>A growth assessment: WHO z-score (2 decimals, as WHO Anthro), percentile and classification.</summary>
public sealed record GrowthResult(GrowthIndicator Indicator, double Value, double ZScore, double Percentile, Localized Classification, bool Flagged, string Reference)
{
    public override string ToString() => FormattableString.Invariant($"{Indicator}: z {ZScore:+0.00;-0.00} (P{Percentile:0.#}) — {Classification.En}");
}

/// <summary>Measurement position for length / height.</summary>
public enum LengthMeasure { Recumbent, Standing }

/// <summary>
/// WHO growth assessment with WHO's own algorithms (as implemented in WHO Anthro / AnthroPlus): z-scores from the LMS
/// tables, the restricted computation beyond ±3 SD for weight-based indicators, the 0.7 cm recumbent ↔ standing
/// conversion at 24 months, interpolation for weight-for-length/height and for ages in months (5–19 y), and the
/// biologically-implausible flags.
/// <para>
/// The tables are <b>not embedded</b> (they are published by WHO under their own terms): load them from a folder with
/// <see cref="Load"/>, or let <see cref="DownloadAsync"/> fetch them from WHO's GitHub repositories (anthro / anthroplus,
/// pinned commits) into a local cache.
/// </para>
/// </summary>
public sealed class WhoGrowthStandards
{
    private const string AnthroBase = "https://raw.githubusercontent.com/WorldHealthOrganization/anthro/b776d8a12b1c97369c748b561159fd2ec4f4db58/data-raw/growthstandards/";
    private const string AnthroPlusBase = "https://raw.githubusercontent.com/WorldHealthOrganization/anthroplus/7cfcdb39026e9a55de55732bc3cf14c82261bcf7/data-raw/growthstandards/";

    /// <summary>Table files and their source URL.</summary>
    public static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>
    {
        ["weianthro.txt"] = AnthroBase, ["lenanthro.txt"] = AnthroBase, ["bmianthro.txt"] = AnthroBase, ["hcanthro.txt"] = AnthroBase,
        ["acanthro.txt"] = AnthroBase, ["ssanthro.txt"] = AnthroBase, ["tsanthro.txt"] = AnthroBase,
        ["wflanthro.txt"] = AnthroBase, ["wfhanthro.txt"] = AnthroBase,
        ["wfawho2007.txt"] = AnthroPlusBase, ["hfawho2007.txt"] = AnthroPlusBase, ["bfawho2007.txt"] = AnthroPlusBase,
    };

    // key: (file, sex 1/2) → sorted axis → LMS
    private readonly Dictionary<(string File, int Sex), SortedList<double, Lms>> _tables = [];

    private WhoGrowthStandards() { }

    /// <summary>Loads the WHO table files (tab-separated: sex, age|length|height, l, m, s[, lorh]) from a folder.</summary>
    public static WhoGrowthStandards Load(string directory)
    {
        var g = new WhoGrowthStandards();
        foreach (var file in Files.Keys)
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var p = line.Split('\t');
                if (p.Length < 5) continue;
                int sex = int.Parse(p[0], CultureInfo.InvariantCulture);
                double x = double.Parse(p[1], CultureInfo.InvariantCulture);
                var lms = new Lms(double.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture), double.Parse(p[4], CultureInfo.InvariantCulture));
                if (!g._tables.TryGetValue((file, sex), out var t)) g._tables[(file, sex)] = t = [];
                t[x] = lms;
            }
        }
        if (g._tables.Count == 0) throw new FileNotFoundException($"No WHO growth tables found in '{directory}'. Use WhoGrowthStandards.DownloadAsync.");
        return g;
    }

    /// <summary>Downloads the WHO tables (pinned versions) into <paramref name="cacheDirectory"/> unless already present, then loads them.</summary>
    public static async Task<WhoGrowthStandards> DownloadAsync(string cacheDirectory, HttpClient? http = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        var client = http ?? new HttpClient();
        try
        {
            foreach (var (file, baseUrl) in Files)
            {
                string path = Path.Combine(cacheDirectory, file);
                if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
                var text = await client.GetStringAsync(baseUrl + file, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(path + ".part", text, ct).ConfigureAwait(false);
                File.Move(path + ".part", path, overwrite: true);
            }
        }
        finally { if (http is null) client.Dispose(); }
        return Load(cacheDirectory);
    }

    /// <summary>The indicators available for an age (days), given the loaded tables.</summary>
    public bool Has(string file) => _tables.ContainsKey((file, 1));

    private Lms Lookup(string file, int sex, double x, bool interpolate, double step)
    {
        if (!_tables.TryGetValue((file, sex), out var t)) throw new InvalidOperationException($"Table {file} is not loaded.");
        if (!interpolate)
        {
            if (t.TryGetValue(x, out var exact)) return exact;
            throw new ArgumentOutOfRangeException(nameof(x), $"{x} is outside {file}.");
        }
        double low = Math.Floor(x / step + 1e-9) * step, diff = (x - low) / step;
        low = Math.Round(low, 1);
        if (!t.TryGetValue(low, out var a)) throw new ArgumentOutOfRangeException(nameof(x), $"{x} is outside {file}.");
        if (diff < 1e-9) return a;
        if (!t.TryGetValue(Math.Round(low + step, 1), out var b)) throw new ArgumentOutOfRangeException(nameof(x), $"{x} is outside {file}.");
        return new Lms(a.L + diff * (b.L - a.L), a.M + diff * (b.M - a.M), a.S + diff * (b.S - a.S));
    }

    private static int Sex(Gender g) => g switch { Gender.Male => 1, Gender.Female => 2, _ => throw new ArgumentException("Sex must be female or male.") };

    /// <summary>Age in whole days as WHO Anthro computes it (rounding half up); months are converted with 30.4375 days.</summary>
    public static int AgeInDays(double days) => (int)Math.Floor(days + 0.5);

    /// <summary>Age in days between two dates.</summary>
    public static int AgeInDays(DateOnly birth, DateOnly measured) => measured.DayNumber - birth.DayNumber;

    /// <summary>
    /// Assesses one measurement. <paramref name="ageDays"/> drives the 0–5 y standards (&lt; 60 months); older children use
    /// the 2007 reference with age in months (60–228). For <see cref="GrowthIndicator.WeightForLengthHeight"/> pass the length
    /// / height in <paramref name="lengthCm"/>; for BMI pass weight and length/height.
    /// </summary>
    public GrowthResult Assess(GrowthIndicator indicator, Gender sex, double ageDays, double value, double? lengthCm = null,
        LengthMeasure? measure = null)
    {
        int s = Sex(sex);
        int days = AgeInDays(ageDays);
        double ageMonths = ageDays / 30.4375;
        // WHO Anthro treats a standing measurement below 9 months as implausible (position unknown, no conversion).
        if (measure == LengthMeasure.Standing && ageMonths < 9) measure = null;
        // Recumbent length below 731 days, standing height from 731 days (WHO: ± 0.7 cm); unknown position: no conversion.
        double? lenhei = lengthCm is { } lc
            ? (days < 731 && measure == LengthMeasure.Standing ? lc + 0.7 : days >= 731 && measure == LengthMeasure.Recumbent ? lc - 0.7 : lc)
            : null;
        bool child = ageMonths < 60;
        if (!child && ageMonths >= 229) throw new ArgumentOutOfRangeException(nameof(ageDays), "WHO growth references cover 0–19 years (to 228 months).");
        double z;
        string reference;
        switch (indicator)
        {
            case GrowthIndicator.WeightForAge when child:
                z = Lookup("weianthro.txt", s, days, false, 1).ZRestricted(value); reference = "WHO Child Growth Standards 2006"; break;
            case GrowthIndicator.WeightForAge:
                if (ageMonths >= 121) throw new ArgumentOutOfRangeException(nameof(ageDays), "WHO weight-for-age is not defined beyond 10 years; use BMI-for-age.");
                z = Lookup("wfawho2007.txt", s, ageMonths, true, 1).ZRestricted(value); reference = "WHO Growth Reference 2007"; break;
            case GrowthIndicator.LengthHeightForAge when child:
                z = Lookup("lenanthro.txt", s, days, false, 1).Z(lenhei ?? value); value = lenhei ?? value; reference = "WHO Child Growth Standards 2006"; break;
            case GrowthIndicator.LengthHeightForAge:
                z = Lookup("hfawho2007.txt", s, ageMonths, true, 1).Z(value); reference = "WHO Growth Reference 2007"; break;
            case GrowthIndicator.BmiForAge:
            {
                double bmi = lenhei is { } h ? value / (h / 100 * (h / 100)) : value;
                value = Math.Round(bmi, 2);
                z = child ? Lookup("bmianthro.txt", s, days, false, 1).ZRestricted(bmi) : Lookup("bfawho2007.txt", s, ageMonths, true, 1).ZRestricted(bmi);
                reference = child ? "WHO Child Growth Standards 2006" : "WHO Growth Reference 2007";
                break;
            }
            case GrowthIndicator.WeightForLengthHeight:
            {
                double h = lenhei ?? throw new ArgumentException("Length / height is required for weight-for-length/height.", nameof(lengthCm));
                string file = days < 731 ? "wflanthro.txt" : "wfhanthro.txt";
                z = Lookup(file, s, h, true, 0.1).ZRestricted(value);
                reference = "WHO Child Growth Standards 2006";
                break;
            }
            case GrowthIndicator.HeadCircumferenceForAge when child:
                z = Lookup("hcanthro.txt", s, days, false, 1).Z(value); reference = "WHO Child Growth Standards 2006"; break;
            case GrowthIndicator.ArmCircumferenceForAge when child:
                z = Lookup("acanthro.txt", s, days, false, 1).ZRestricted(value); reference = "WHO Child Growth Standards 2006"; break;
            case GrowthIndicator.SubscapularSkinfoldForAge when child:
                z = Lookup("ssanthro.txt", s, days, false, 1).ZRestricted(value); reference = "WHO Child Growth Standards 2006"; break;
            case GrowthIndicator.TricepsSkinfoldForAge when child:
                z = Lookup("tsanthro.txt", s, days, false, 1).ZRestricted(value); reference = "WHO Child Growth Standards 2006"; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(indicator), $"{indicator} is only defined for 0–5 years.");
        }
        z = Math.Round(z, 2, MidpointRounding.AwayFromZero);
        bool flagged = indicator switch
        {
            GrowthIndicator.WeightForAge => z < -6 || z > 5,
            GrowthIndicator.LengthHeightForAge => Math.Abs(z) > 6,
            _ => Math.Abs(z) > 5,
        };
        return new GrowthResult(indicator, value, z, Math.Round(100 * NormalCdf(z), 1), Classify(indicator, z, child), flagged, reference);
    }

    /// <summary>Values of the −3 … +3 SD lines at an axis value (age in days, or length in cm for weight-for-length/height).</summary>
    public IReadOnlyList<(int Sd, double Value)> SdLines(GrowthIndicator indicator, Gender sex, double axis)
    {
        string file = indicator switch
        {
            GrowthIndicator.WeightForAge => "weianthro.txt", GrowthIndicator.LengthHeightForAge => "lenanthro.txt", GrowthIndicator.BmiForAge => "bmianthro.txt",
            GrowthIndicator.HeadCircumferenceForAge => "hcanthro.txt", GrowthIndicator.WeightForLengthHeight => axis < 87 ? "wflanthro.txt" : "wfhanthro.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(indicator)),
        };
        var lms = indicator == GrowthIndicator.WeightForLengthHeight ? Lookup(file, Sex(sex), axis, true, 0.1) : Lookup(file, Sex(sex), AgeInDays(axis), false, 1);
        return Enumerable.Range(-3, 7).Select(sd => (sd, Math.Round(lms.ValueAt(sd), 3))).ToList();
    }

    private static Localized Classify(GrowthIndicator i, double z, bool child) => i switch
    {
        GrowthIndicator.LengthHeightForAge => z < -3 ? new("Severely stunted (< −3 SD)", "Sangat pendek (< −3 SD)") : z < -2 ? new("Stunted (< −2 SD)", "Pendek / stunting (< −2 SD)")
            : z > 3 ? new("Very tall (> +3 SD) — check for endocrine disorder", "Sangat tinggi (> +3 SD) — periksa kelainan endokrin") : new("Normal", "Normal"),
        GrowthIndicator.WeightForAge => z < -3 ? new("Severely underweight (< −3 SD)", "Berat badan sangat kurang (< −3 SD)") : z < -2 ? new("Underweight (< −2 SD)", "Berat badan kurang (< −2 SD)")
            : z > 1 ? new("Possible growth problem (> +1 SD) — assess weight-for-length/height or BMI", "Kemungkinan masalah pertumbuhan (> +1 SD) — nilai BB/PB atau IMT") : new("Normal", "Normal"),
        GrowthIndicator.WeightForLengthHeight or GrowthIndicator.BmiForAge when child => z < -3 ? new("Severely wasted (< −3 SD)", "Gizi buruk (< −3 SD)") : z < -2 ? new("Wasted (< −2 SD)", "Gizi kurang (< −2 SD)")
            : z > 3 ? new("Obese (> +3 SD)", "Obesitas (> +3 SD)") : z > 2 ? new("Overweight (> +2 SD)", "Gizi lebih (> +2 SD)") : z > 1 ? new("Possible risk of overweight (> +1 SD)", "Berisiko gizi lebih (> +1 SD)") : new("Normal", "Gizi baik"),
        GrowthIndicator.BmiForAge => z < -3 ? new("Severe thinness (< −3 SD)", "Gizi buruk (< −3 SD)") : z < -2 ? new("Thinness (< −2 SD)", "Gizi kurang (< −2 SD)")
            : z > 2 ? new("Obesity (> +2 SD)", "Obesitas (> +2 SD)") : z > 1 ? new("Overweight (> +1 SD)", "Gizi lebih (> +1 SD)") : new("Normal", "Gizi baik"),
        GrowthIndicator.HeadCircumferenceForAge => z < -2 ? new("Microcephaly (< −2 SD)", "Mikrosefali (< −2 SD)") : z > 2 ? new("Macrocephaly (> +2 SD)", "Makrosefali (> +2 SD)") : new("Normal", "Normal"),
        _ => z < -2 ? new("Below −2 SD", "Di bawah −2 SD") : z > 2 ? new("Above +2 SD", "Di atas +2 SD") : new("Normal", "Normal"),
    };

    /// <summary>Standard normal CDF (Abramowitz–Stegun 7.1.26 via erf, |error| &lt; 1.5e-7).</summary>
    public static double NormalCdf(double z)
    {
        double t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2));
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z / 2);
        return z >= 0 ? 0.5 * (1 + y) : 0.5 * (1 - y);
    }
}
