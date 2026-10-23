using System.Collections.Concurrent;
using System.Reflection;

namespace CesiumNet.Gallery;

/// <summary>Gallery UI language.</summary>
public enum GalleryLanguage { En, Id }

/// <summary>A string in English and Bahasa Indonesia.</summary>
public sealed record Text(string En, string Id)
{
    public string this[GalleryLanguage lang] => lang == GalleryLanguage.Id ? Id : En;
    public static implicit operator Text((string En, string Id) t) => new(t.En, t.Id);
}

/// <summary>A button shown next to a running sample.</summary>
public sealed record SampleAction(string Key, Text Label, Func<SampleContext, Task> Run)
{
    /// <summary>Optional group label for visually grouping related actions.</summary>
    public Text? Group { get; init; }
}

/// <summary>Everything a sample needs while it runs.</summary>
public sealed class SampleContext(CesiumController cesium, Action<string> log, CancellationToken cancellationToken)
{
    public CesiumController Cesium { get; } = cesium;
    public CancellationToken Token { get; } = cancellationToken;
    /// <summary>Optional Cesium ion token (enables World Terrain, OSM Buildings, ion assets).</summary>
    public string? IonToken { get; init; }
    /// <summary>Shared HTTP client for samples that fetch data.</summary>
    public HttpClient Http { get; init; } = SharedHttp.Client;
    /// <summary>Per-run scratch state (ids of created objects, toggles…).</summary>
    public Dictionary<string, object?> State { get; } = [];
    /// <summary>Disposables cleaned up when the sample stops.</summary>
    public List<IAsyncDisposable> Resources { get; } = [];
    public bool HasIonToken => !string.IsNullOrWhiteSpace(IonToken);

    /// <summary>Writes a line to the sample's event log.</summary>
    public void Log(string message) => log(message);

    public T Get<T>(string key, T fallback = default!) => State.TryGetValue(key, out var v) && v is T t ? t : fallback;
}

internal static class SharedHttp
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
}

/// <summary>Base class for gallery samples. One sample = one use case.</summary>
public abstract class GallerySample
{
    /// <summary>URL-friendly id, e.g. <c>hello-globe</c>.</summary>
    public abstract string Id { get; }
    public abstract string CategoryId { get; }
    public abstract Text Title { get; }
    public abstract Text Summary { get; }
    /// <summary>Where the sample takes place (shown in the sample index).</summary>
    public virtual GeoPoint? Location => null;
    /// <summary>Shown as a tag when the sample needs a Cesium ion token for full effect.</summary>
    public virtual bool UsesIon => false;
    public virtual IReadOnlyList<SampleAction> Actions => [];

    /// <summary>Viewer options for this sample.</summary>
    public virtual CesiumViewerOptions CreateOptions(string? ionToken) => GalleryDefaults.Options(ionToken);

    /// <summary>Runs once the viewer is ready.</summary>
    public abstract Task RunAsync(SampleContext ctx);

    /// <summary>The sample's source file (as embedded), trimmed to the <c>// snippet</c> region when present.</summary>
    public string SourceCode => SampleSources.Get(GetType());
}

/// <summary>Shared defaults used by the samples.</summary>
public static class GalleryDefaults
{
    public static CesiumViewerOptions Options(string? ionToken, Action<CesiumViewerOptions>? configure = null)
    {
        var o = new CesiumViewerOptions
        {
            IonAccessToken = string.IsNullOrWhiteSpace(ionToken) ? null : ionToken,
            BaseLayer = ImageryLayerOptions.Satellite(),
            InitialView = CameraView.At(117.5, -2.5, 9_500_000),
            InfoBox = true,
            SelectionIndicator = true,
            Events = new CesiumEventOptions { Click = true, CameraChanged = true },
        };
        configure?.Invoke(o);
        return o;
    }

    /// <summary>Monas (National Monument), Jakarta — the gallery's reference point.</summary>
    public static readonly GeoPoint Monas = new(106.827153, -6.175392, 0);
}

/// <summary>Reads embedded sample sources.</summary>
public static class SampleSources
{
    private static readonly ConcurrentDictionary<Type, string> s_cache = new();

    public static string Get(Type sampleType) => s_cache.GetOrAdd(sampleType, static t =>
    {
        var asm = t.Assembly;
        // %(RecursiveDir) yields '\' on Windows, so normalise separators before matching.
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/').EndsWith("/" + t.Name + ".cs", StringComparison.Ordinal));
        if (name is null) return "// Source not available.";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n");
        var start = text.IndexOf("// <snippet>", StringComparison.Ordinal);
        var end = text.IndexOf("// </snippet>", StringComparison.Ordinal);
        if (start < 0 || end < start) return text.Trim();
        var body = text[(text.IndexOf('\n', start) + 1)..end];
        return Dedent(body).Trim('\n');
    });

    private static string Dedent(string block)
    {
        var lines = block.Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }
}

/// <summary>A category of samples.</summary>
public sealed record SampleCategory(string Id, Text Title, Text Blurb);

/// <summary>The gallery catalog: categories and samples discovered by reflection.</summary>
public static class SampleCatalog
{
    public static IReadOnlyList<SampleCategory> Categories { get; } =
    [
        new("start", ("Getting started", "Memulai"), ("A globe in a few lines of C#.", "Globe 3D dengan beberapa baris C#.")),
        new("imagery", ("Imagery & maps", "Citra & peta"), ("Satellite, streets, WMS, WMTS, XYZ, blending.", "Satelit, jalan, WMS, WMTS, XYZ, blending.")),
        new("terrain", ("Terrain & elevation", "Terrain & elevasi"), ("Elevation, exaggeration, profiles, clipping.", "Elevasi, eksagerasi, profil, clipping.")),
        new("tiles", ("3D Tiles", "3D Tiles"), ("Massive cities, point clouds, BIM, styling.", "Kota masif, point cloud, BIM, styling.")),
        new("data", ("Models & data", "Model & data"), ("glTF, GeoJSON, TopoJSON, KML, CZML, GPX.", "glTF, GeoJSON, TopoJSON, KML, CZML, GPX.")),
        new("gis", ("Entities & GIS", "Entitas & GIS"), ("Shapes, picking, coordinates, spatial queries.", "Bentuk, picking, koordinat, kueri spasial.")),
        new("draw", ("Drawing & measuring", "Menggambar & mengukur"), ("Sketch, edit, measure distance and area.", "Sketsa, edit, ukur jarak dan luas.")),
        new("camera", ("Camera", "Kamera"), ("Fly-to tours, orbit, first-person flight.", "Tur fly-to, orbit, terbang orang pertama.")),
        new("time", ("Time & tracking", "Waktu & pelacakan"), ("Timelines, simulations, live fleets.", "Timeline, simulasi, armada real-time.")),
        new("aerospace", ("Aerospace & defense", "Dirgantara & pertahanan"), ("Orbits, sensors, operational pictures.", "Orbit, sensor, gambaran operasional.")),
        new("effects", ("Visual effects", "Efek visual"), ("Lighting, shadows, atmosphere, weather.", "Pencahayaan, bayangan, atmosfer, cuaca.")),
        new("advanced", ("Performance & integration", "Performa & integrasi"), ("100k points, Cesium ion, screenshots, JS.", "100rb titik, Cesium ion, screenshot, JS.")),
    ];

    private static readonly Lazy<IReadOnlyList<GallerySample>> s_samples = new(() =>
        typeof(SampleCatalog).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(GallerySample).IsAssignableFrom(t))
            .Select(t => (GallerySample)Activator.CreateInstance(t)!)
            .OrderBy(s => Categories.ToList().FindIndex(c => c.Id == s.CategoryId))
            .ThenBy(s => s.GetType().GetCustomAttribute<SampleOrderAttribute>()?.Order ?? 100)
            .ToList());

    public static IReadOnlyList<GallerySample> Samples => s_samples.Value;

    public static GallerySample? Find(string? id) => Samples.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<GallerySample> InCategory(string categoryId) => Samples.Where(s => s.CategoryId == categoryId);
}

/// <summary>Orders samples within a category.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SampleOrderAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>Shared copy for both gallery front-ends.</summary>
public static class GalleryText
{
    public static readonly Text Credit = ("Made by Gravicode Studios, led by Kang Fadhil", "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil");
    public static readonly Text Overview = ("Overview", "Ringkasan");
    public static readonly Text Code = ("Code", "Kode");
    public static readonly Text Events = ("Event log", "Log peristiwa");
    public static readonly Text Actions = ("Try it", "Coba");
    public static readonly Text Search = ("Search samples", "Cari contoh");
    public static readonly Text Copy = ("Copy code", "Salin kode");
    public static readonly Text Copied = ("Copied", "Tersalin");
    public static readonly Text Restart = ("Restart sample", "Mulai ulang");
    public static readonly Text NoEvents = ("Click the globe or use an action — events appear here.", "Klik globe atau jalankan aksi — peristiwa muncul di sini.");
    public static readonly Text IonTag = ("Better with Cesium ion", "Lebih baik dengan Cesium ion");
    public static readonly Text IonHint = ("Add a free Cesium ion token in settings to unlock World Terrain, OSM Buildings and ion assets.", "Tambahkan token Cesium ion gratis di pengaturan untuk membuka World Terrain, OSM Buildings, dan aset ion.");
    public static readonly Text Loading = ("Loading CesiumJS…", "Memuat CesiumJS…");
    public static readonly Text LoadFailed = ("CesiumJS could not load. Check your internet connection — the library is fetched from cdn.jsdelivr.net.", "CesiumJS gagal dimuat. Periksa koneksi internet — pustaka diambil dari cdn.jsdelivr.net.");
    public static readonly Text Camera = ("Camera", "Kamera");
    public static readonly Text Samples = ("samples", "contoh");
    public static readonly Text Settings = ("Settings", "Pengaturan");
    public static readonly Text IonToken = ("Cesium ion access token", "Token akses Cesium ion");
    public static readonly Text Save = ("Save", "Simpan");
    public static readonly Text Tagline = ("CesiumJS for .NET — one C# API for Blazor and Avalonia.", "CesiumJS untuk .NET — satu API C# untuk Blazor dan Avalonia.");
}
