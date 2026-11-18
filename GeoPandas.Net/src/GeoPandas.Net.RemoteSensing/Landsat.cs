using System.Globalization;
using System.Text.Json;
using GeoPandas.Net.Raster;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.RemoteSensing;

/// <summary>
/// USGS Landsat Collection 2 scene (Level-1 and Level-2 science products) from its MTL metadata (<c>_MTL.txt</c> or <c>_MTL.json</c>)
/// and GeoTIFF bands: scaled surface/TOA reflectance, surface temperature, QA_PIXEL cloud masks and spectral indices.
/// </summary>
public sealed class LandsatScene
{
    private readonly Dictionary<string, string> _mtl; // "GROUP.KEY" and "KEY" → value
    private readonly string _dir;

    private LandsatScene(string dir, Dictionary<string, string> mtl) { _dir = dir; _mtl = mtl; }

    /// <summary>Opens a scene from its MTL file or from the folder that contains it.</summary>
    public static LandsatScene Open(string mtlOrFolder)
    {
        string path = Directory.Exists(mtlOrFolder)
            ? Directory.EnumerateFiles(mtlOrFolder, "*_MTL.txt").Concat(Directory.EnumerateFiles(mtlOrFolder, "*_MTL.json")).FirstOrDefault()
              ?? throw new FileNotFoundException($"No *_MTL.txt / *_MTL.json in '{mtlOrFolder}'.")
            : mtlOrFolder;
        var mtl = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ParseJson(File.ReadAllText(path)) : ParseOdl(File.ReadAllLines(path));
        return new LandsatScene(Path.GetDirectoryName(Path.GetFullPath(path))!, mtl);
    }

    /// <summary>Parses ODL "GROUP = … / KEY = VALUE / END_GROUP" text into "GROUP.KEY" entries (the innermost group).</summary>
    public static Dictionary<string, string> ParseOdl(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Stack<string>();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l == "END") continue;
            int eq = l.IndexOf('=');
            if (eq < 0) continue;
            string k = l[..eq].Trim(), v = l[(eq + 1)..].Trim().Trim('"');
            if (k == "GROUP") { groups.Push(v); continue; }
            if (k == "END_GROUP") { if (groups.Count > 0) groups.Pop(); continue; }
            string g = groups.Count > 0 ? groups.Peek() : "";
            d[$"{g}.{k}"] = v;
            d.TryAdd(k, v);
        }
        return d;
    }

    private static Dictionary<string, string> ParseJson(string json)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonElement e, string group)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object) Walk(p.Value, p.Name);
                else
                {
                    var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                    d[$"{group}.{p.Name}"] = v;
                    d.TryAdd(p.Name, v);
                }
            }
        }
        Walk(JsonDocument.Parse(json).RootElement, "");
        return d;
    }

    public string? Get(string key) => _mtl.TryGetValue(key, out var v) ? v : null;
    private double? Num(string key) => Get(key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public string ProductId => Get("LANDSAT_PRODUCT_ID") ?? Get("LANDSAT_SCENE_ID") ?? "";
    public string Spacecraft => Get("SPACECRAFT_ID") ?? "";
    public string Sensor => Get("SENSOR_ID") ?? "";
    /// <summary>"L1TP", "L2SP", "L2SR"…</summary>
    public string ProcessingLevel => Get("PROCESSING_LEVEL") ?? (ProductId.Contains("_L2", StringComparison.Ordinal) ? "L2SP" : "L1TP");
    public bool IsLevel2 => ProcessingLevel.StartsWith("L2", StringComparison.OrdinalIgnoreCase);
    public DateTime Acquired
    {
        get
        {
            var date = DateTime.Parse(Get("DATE_ACQUIRED") ?? "2000-01-01", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (Get("SCENE_CENTER_TIME") is { } t && TimeSpan.TryParse(t.TrimEnd('Z').Split('.')[0], CultureInfo.InvariantCulture, out var ts)) date = date.Date + ts;
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        }
    }
    public double? CloudCover => Num("CLOUD_COVER");
    public double? SunElevation => Num("SUN_ELEVATION");
    public double? SunAzimuth => Num("SUN_AZIMUTH");
    /// <summary>OLI/TIRS (Landsat 8/9) versus TM/ETM+ band numbering.</summary>
    public bool IsOli => Sensor.Contains("OLI", StringComparison.OrdinalIgnoreCase) || Spacecraft is "LANDSAT_8" or "LANDSAT_9";

    /// <summary>Path of a band file ("1".."11", "ST_B10", "QA_PIXEL"…).</summary>
    public string BandPath(string band)
    {
        var key = band.StartsWith("QA", StringComparison.OrdinalIgnoreCase) ? (band.Equals("QA_PIXEL", StringComparison.OrdinalIgnoreCase) ? "FILE_NAME_QUALITY_L1_PIXEL" : $"FILE_NAME_QUALITY_{band}") : $"FILE_NAME_BAND_{band}";
        var name = Get(key) ?? throw new KeyNotFoundException($"MTL has no {key}.");
        return Path.Combine(_dir, name);
    }

    /// <summary>Raw digital numbers of a band.</summary>
    public RasterGrid Band(string band) => GeoTiff.Read(BandPath(band));

    /// <summary>
    /// Reflectance (unitless) of an optical band: Level-2 surface reflectance (×2.75e-5 − 0.2) or Level-1 top-of-atmosphere reflectance
    /// corrected for the sun elevation. Fill value 0 becomes NaN.
    /// </summary>
    public RasterGrid Reflectance(int band)
    {
        var r = Band(band.ToString(CultureInfo.InvariantCulture));
        string grp = IsLevel2 ? "LEVEL2_SURFACE_REFLECTANCE_PARAMETERS" : "LEVEL1_RADIOMETRIC_RESCALING";
        double mult = Num($"{grp}.REFLECTANCE_MULT_BAND_{band}") ?? (IsLevel2 ? 2.75e-5 : throw new KeyNotFoundException($"No reflectance scaling for band {band}."));
        double add = Num($"{grp}.REFLECTANCE_ADD_BAND_{band}") ?? (IsLevel2 ? -0.2 : 0);
        double sun = IsLevel2 ? 1 : Math.Sin((SunElevation ?? 90) * Math.PI / 180);
        var res = r.Map(v => v == 0 || float.IsNaN(v) ? float.NaN : (float)((v * mult + add) / sun));
        res.BandNames = [$"B{band}"];
        return res;
    }

    /// <summary>Level-2 surface temperature in kelvin (ST_B10 for Landsat 8/9, ST_B6 for 4–7).</summary>
    public RasterGrid SurfaceTemperature()
    {
        string b = IsOli ? "ST_B10" : "ST_B6";
        var r = Band(b);
        double mult = Num($"LEVEL2_SURFACE_TEMPERATURE_PARAMETERS.TEMPERATURE_MULT_BAND_{b}") ?? 0.00341802;
        double add = Num($"LEVEL2_SURFACE_TEMPERATURE_PARAMETERS.TEMPERATURE_ADD_BAND_{b}") ?? 149.0;
        return r.Map(v => v == 0 ? float.NaN : (float)(v * mult + add));
    }

    /// <summary>Mask (1 = clear) from QA_PIXEL bits: fill (0), dilated cloud (1), cirrus (2), cloud (3), cloud shadow (4), optionally snow (5).</summary>
    public RasterGrid ClearMask(bool maskSnow = false)
    {
        var qa = Band("QA_PIXEL");
        int bits = 0b1_1111 | (maskSnow ? 1 << 5 : 0);
        return qa.Map(v => ((int)v & bits) == 0 ? 1f : 0f);
    }

    /// <summary>NDVI from the red and near-infrared reflectance (B4/B5 on OLI, B3/B4 on TM/ETM+), clouds masked.</summary>
    public RasterGrid Ndvi(bool maskClouds = true)
    {
        var (red, nir) = IsOli ? (4, 5) : (3, 4);
        var ndvi = RasterGrid.Combine(Reflectance(nir), Reflectance(red), (n, r) => n + r == 0 ? float.NaN : (n - r) / (n + r));
        if (maskClouds && Get("FILE_NAME_QUALITY_L1_PIXEL") is not null)
            ndvi = RasterGrid.Combine(ndvi, ClearMask(), (v, m) => m > 0 ? v : float.NaN);
        ndvi.BandNames = ["ndvi"];
        return ndvi;
    }

    public override string ToString() => $"{Spacecraft} {Sensor} {ProcessingLevel} {Acquired:yyyy-MM-dd HH:mm}Z cloud {CloudCover:F1}% ({ProductId})";
}
