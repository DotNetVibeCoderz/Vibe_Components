using System.Text.RegularExpressions;
using AquaCulture.ComputerVision;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.GIS;
using AquaCulture.Visualization;

namespace AquaCulture.Gallery.Shared;

/// <summary>
/// Reference farm used across demos: "Tambak Mina Sejahtera", a block of six HDPE-lined whiteleg-shrimp ponds on the north coast of
/// West Java (Indramayu). Everything is synthetic and deterministic so screenshots and tests are reproducible.
/// </summary>
public static class DemoFarm
{
    public static readonly GeoCoordinate Origin = new(-6.3330, 108.3210, 2);
    public static readonly DateOnly Today = new(2026, 11, 12);
    public static readonly DateTime Now = new(2026, 11, 12, 19, 30, 0);
    public const string Name = "Tambak Mina Sejahtera";
    public const string Place = "Indramayu, Jawa Barat";

    private static readonly Lazy<Farm> _farm = new(Build);
    public static Farm Farm => _farm.Value;
    public static SpeciesProfile Species => SpeciesCatalog.Vaname;

    private static readonly (int Doc, long Stocked, double Abw, double SrFactor)[] Profiles =
        [(62, 260_000, 11.8, 0.86), (75, 240_000, 15.6, 0.83), (48, 270_000, 7.4, 0.9), (90, 230_000, 19.9, 0.78), (30, 280_000, 2.9, 0.95), (68, 250_000, 13.2, 0.8)];

    private static Farm Build()
    {
        var farm = new Farm { Id = "MS-01", Name = Name, Type = FarmType.ShrimpFarm, Location = Origin, Owner = "Kelompok Pembudidaya Mina Sejahtera" };
        var ponds = FarmMapping.PondBlock(Origin, 2, 3, 40, 50, 5, 1.3, CultureSystem.LinedPond, "K");
        foreach (var p in ponds) { p.AerationKw = 4.4; farm.Ponds.Add(p); }
        for (int i = 0; i < ponds.Count; i++)
        {
            var (doc, stocked, abw, sr) = Profiles[i];
            var c = new CultureCycle
            {
                Id = $"S{i + 1:00}-2026B", PondId = ponds[i].Id, Species = Species, StockingDate = Today.AddDays(-doc), StockedCount = stocked,
                StockingWeightG = 0.002, SeedPricePerUnit = 45,
            };
            for (int w = 4; w * 7 <= doc; w++)
            {
                double frac = (double)(w * 7) / doc;
                c.LogSampling(c.StockingDate.AddDays(w * 7), Math.Round(abw * Math.Pow(frac, 1.6), 2));
            }
            c.LogSampling(Today.AddDays(-1), abw);
            long deaths = (long)(stocked * (1 - sr));
            c.LogMortality(c.StockingDate.AddDays(doc / 3), deaths / 2).LogMortality(c.StockingDate.AddDays(2 * doc / 3), deaths - deaths / 2);
            for (int d = 1; d <= doc; d++)
            {
                double w = abw * Math.Pow((double)d / doc, 1.6);
                double alive = stocked * (1 - (1 - sr) * d / doc);
                double kg = Math.Max(1.5, alive * Math.Max(w, 0.05) / 1000 * Species.FeedingRatePercent(Math.Max(w, 0.05)) / 100 * 0.92);
                c.LogFeed(c.StockingDate.AddDays(d), Math.Round(kg, 1), w < 2 ? "shrimp-crumble" : w < 10 ? "shrimp-grower" : "shrimp-finisher", w < 2 ? 22_000 : w < 10 ? 18_500 : 16_500);
            }
            c.LogExpense(c.StockingDate, "Listrik kincir (energy)", doc * 18 * ponds[i].AerationKw * 1_500)
             .LogExpense(c.StockingDate, "Tenaga kerja (labour)", doc * 85_000)
             .LogExpense(c.StockingDate, "Probiotik & kapur (chemicals)", doc * 32_000)
             .LogExpense(c.StockingDate, "Lain-lain", 2_500_000);
            farm.Cycles.Add(c);
        }
        return farm;
    }

    /// <summary>Hourly weather from tonight for 3 days (deterministic).</summary>
    public static IReadOnlyList<WeatherObservation> Weather(int days = 3, ulong seed = 12, double wet = 1) =>
        SyntheticWeather.Generate(Origin.Latitude, Today.ToDateTime(TimeOnly.MinValue), days, seed, 28, wet);

    public static GeoBounds Bounds
    {
        get
        {
            var all = Farm.Ponds.SelectMany(p => p.Outline!.Ring).ToList();
            return new GeoBounds(all.Min(c => c.Latitude), all.Min(c => c.Longitude), all.Max(c => c.Latitude), all.Max(c => c.Longitude)).Expand(0.00025);
        }
    }
}

/// <summary>Resolves CSS custom properties in generated SVG for renderers without CSS (Avalonia / Svg.Skia).</summary>
public static partial class SvgTheme
{
    [GeneratedRegex(@"var\(--[a-z0-9-]+,\s*(#[0-9a-fA-F]{3,8})\)")]
    private static partial Regex VarRegex();

    public static string Resolve(string svg, bool dark = false)
    {
        var s = VarRegex().Replace(svg, m => m.Groups[1].Value).Replace("font-family=\"inherit\"", "font-family=\"Public Sans, Segoe UI, Arial, sans-serif\"");
        if (dark) s = s.Replace("#0b2e36", "#e4f1ef").Replace("#5b7279", "#93aaae").Replace("#d3e0de", "#23393e");
        return s;
    }
}

/// <summary>Image helpers shared by the gallery apps.</summary>
public static class GalleryImages
{
    public static byte[] Png(RgbImage img)
    {
        var px = new Rgba[img.Width * img.Height];
        for (int i = 0; i < px.Length; i++) px[i] = new Rgba(img.Data[i * 3], img.Data[i * 3 + 1], img.Data[i * 3 + 2]);
        return PngEncoder.Encode(px, img.Width, img.Height);
    }
}

/// <summary>Formatting helpers.</summary>
public static class Fmt
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    public static string N(double v, string f = "0.0") => double.IsFinite(v) ? v.ToString(f, Inv) : "–";
    public static string Rp(double v) => double.IsFinite(v) ? "Rp " + v.ToString("N0", Inv) : "–";
    public static string Pct(double v, string f = "0") => double.IsFinite(v) ? v.ToString(f, Inv) + " %" : "–";
}
