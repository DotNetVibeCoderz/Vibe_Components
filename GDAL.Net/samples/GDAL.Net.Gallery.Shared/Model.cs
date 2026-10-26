using GDAL.Net.Rendering;

namespace GDAL.Net.Gallery;

/// <summary>Gallery UI language.</summary>
public enum Lang
{
    En,
    Id,
}

/// <summary>A bilingual string (English / Bahasa Indonesia).</summary>
public readonly record struct Text(string En, string Id)
{
    public string this[Lang lang] => lang == Lang.Id ? Id : En;
    public override string ToString() => En;
}

/// <summary>Gallery categories (in display order).</summary>
public enum SampleCategory
{
    Raster,
    Terrain,
    RemoteSensing,
    Projection,
    Vector,
    Mosaic,
    CloudVfs,
    Multidimensional,
    Utilities,
}

public static class CategoryInfo
{
    public static Text Title(SampleCategory c) => c switch
    {
        SampleCategory.Raster => new("Raster data", "Data raster"),
        SampleCategory.Terrain => new("Terrain analysis", "Analisis terrain"),
        SampleCategory.RemoteSensing => new("Remote sensing", "Penginderaan jauh"),
        SampleCategory.Projection => new("Coordinate systems", "Sistem koordinat"),
        SampleCategory.Vector => new("Vector & SQL", "Vektor & SQL"),
        SampleCategory.Mosaic => new("Mosaics & clipping", "Mosaik & pemotongan"),
        SampleCategory.CloudVfs => new("Cloud & virtual files", "Cloud & file virtual"),
        SampleCategory.Multidimensional => new("Multidimensional", "Multidimensi"),
        _ => new("Utilities", "Utilitas"),
    };

    /// <summary>Legend swatch color of the category (hex).</summary>
    public static string Swatch(SampleCategory c) => c switch
    {
        SampleCategory.Raster => "#2A9D8F",
        SampleCategory.Terrain => "#C8553D",
        SampleCategory.RemoteSensing => "#8AB17D",
        SampleCategory.Projection => "#5B7DB1",
        SampleCategory.Vector => "#E09F3E",
        SampleCategory.Mosaic => "#7A6C9D",
        SampleCategory.CloudVfs => "#3D8EB9",
        SampleCategory.Multidimensional => "#B5648A",
        _ => "#6B7F7A",
    };
}

public enum ParameterKind
{
    Choice,
    Number,
    Text,
    Toggle,
}

/// <summary>An interactive parameter of a sample, rendered generically by the gallery UIs.</summary>
public sealed record SampleParameter(string Key, Text Label, ParameterKind Kind, string Default)
{
    public IReadOnlyList<string> Options { get; init; } = [];
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    /// <summary>Multi-line text (expressions, SQL).</summary>
    public bool Multiline { get; init; }
    public Text? Hint { get; init; }

    public static SampleParameter Choice(string key, Text label, string @default, params string[] options) => new(key, label, ParameterKind.Choice, @default) { Options = options };
    public static SampleParameter Number(string key, Text label, double @default, double min, double max, double step = 1)
        => new(key, label, ParameterKind.Number, @default.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Min = min, Max = max, Step = step };
    public static SampleParameter Toggle(string key, Text label, bool @default) => new(key, label, ParameterKind.Toggle, @default ? "true" : "false");
    public static SampleParameter TextBox(string key, Text label, string @default, bool multiline = false) => new(key, label, ParameterKind.Text, @default) { Multiline = multiline };
}

/// <summary>Current parameter values of a run.</summary>
public sealed class SampleArgs(IReadOnlyDictionary<string, string> values)
{
    public string Get(string key, string fallback = "") => values.TryGetValue(key, out var v) ? v : fallback;
    public double Number(string key, double fallback = 0) => double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public bool Flag(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A simple table for the UIs (attribute tables, query results, driver lists).</summary>
public sealed record TableData(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public static TableData From(VectorData data, int maxRows = 500)
    {
        var cols = new List<string> { "FID" };
        cols.AddRange(data.Fields.Select(f => f.Name));
        var rows = data.Features.Take(maxRows)
            .Select(f => (IReadOnlyList<string>)[f.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), .. data.Fields.Select(fd => Format(f[fd.Name]))])
            .ToList();
        return new TableData(cols, rows);
    }

    public static string Format(object? v) => v switch
    {
        null => "∅",
        double d => d.ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture),
        int i => i.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        byte[] b => $"<{b.Length} bytes>",
        _ => v.ToString() ?? string.Empty,
    };
}

/// <summary>A key figure shown in the result panel.</summary>
public sealed record Metric(Text Label, string Value, string? Unit = null);

/// <summary>A raster to display with its rendering options.</summary>
public sealed record RasterLayer(Dataset Dataset, RenderOptions Options, string? Title = null, double? LegendMin = null, double? LegendMax = null);

/// <summary>Everything a sample produced. The UI owns and disposes it.</summary>
public sealed class SampleResult : IDisposable
{
    /// <summary>Raster layers, bottom to top.</summary>
    public List<RasterLayer> Rasters { get; } = [];
    /// <summary>Vector overlays, bottom to top.</summary>
    public List<StyledVector> Vectors { get; } = [];
    public List<Metric> Metrics { get; } = [];
    public TableData? Table { get; set; }
    public Histogram? Histogram { get; set; }
    public Text? HistogramTitle { get; set; }
    /// <summary>Text output (JSON, XML, WKT, CLI output, ...).</summary>
    public string? Output { get; set; }
    /// <summary>Syntax of <see cref="Output"/>: "json", "xml", "text", "wkt", "sql".</summary>
    public string OutputLanguage { get; set; } = "text";
    /// <summary>Preferred map extent (world coordinates of the displayed layers).</summary>
    public Envelope? Extent { get; set; }
    /// <summary>CRS label for the coordinate readout, e.g. "EPSG:32748".</summary>
    public string? Crs { get; set; }
    public Text? Note { get; set; }
    public TimeSpan Elapsed { get; set; }

    /// <summary>Extra disposables kept alive while the result is displayed.</summary>
    public List<IDisposable> Owned { get; } = [];

    public bool HasMap => Rasters.Count > 0 || Vectors.Count > 0;

    public Envelope MapExtent
    {
        get
        {
            if (Extent is { } e) return e;
            var env = Envelope.Empty;
            foreach (var r in Rasters) env = env.Union(RasterRenderer.FullView(r.Dataset));
            foreach (var v in Vectors) env = env.Union(v.Data.Extent);
            return env.IsEmpty ? new Envelope(0, 0, 1, 1) : env;
        }
    }

    public void Dispose()
    {
        foreach (var r in Rasters) r.Dataset.Dispose();
        foreach (var d in Owned) d.Dispose();
        Rasters.Clear();
        Owned.Clear();
    }
}

/// <summary>A gallery use case: description, parameters, runnable code and the code shown to the user.</summary>
public sealed record GallerySample(
    string Id,
    SampleCategory Category,
    Text Title,
    Text Summary,
    string Code,
    Func<SampleContext, SampleArgs, SampleResult> Run)
{
    public IReadOnlyList<SampleParameter> Parameters { get; init; } = [];
    /// <summary>GDAL program / API this sample maps to (e.g. "gdalwarp", "OGR SQL").</summary>
    public string? Equivalent { get; init; }
    public bool RequiresNetwork { get; init; }

    public SampleResult Execute(SampleContext context, IReadOnlyDictionary<string, string>? values = null)
    {
        var dict = Parameters.ToDictionary(p => p.Key, p => p.Default);
        if (values is not null) foreach (var kv in values) dict[kv.Key] = kv.Value;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Run(context, new SampleArgs(dict));
        result.Elapsed = sw.Elapsed;
        return result;
    }
}
