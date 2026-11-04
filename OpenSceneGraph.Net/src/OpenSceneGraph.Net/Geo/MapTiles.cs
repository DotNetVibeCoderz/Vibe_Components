using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Geo;

/// <summary>Slippy-map tile address (Web Mercator, EPSG:3857), as used by OpenStreetMap, WMTS and ArcGIS tile services.</summary>
public readonly record struct TileKey(int Zoom, int X, int Y)
{
    public override string ToString() => $"{Zoom}/{X}/{Y}";
}

/// <summary>Web Mercator tile math.</summary>
public static class TileMath
{
    public static TileKey FromGeo(double lat, double lon, int zoom)
    {
        lat = System.Math.Clamp(lat, -85.05112878, 85.05112878);
        int n = 1 << zoom;
        int x = (int)System.Math.Floor((lon + 180) / 360 * n);
        double latRad = lat * System.Math.PI / 180;
        int y = (int)System.Math.Floor((1 - System.Math.Log(System.Math.Tan(latRad) + 1 / System.Math.Cos(latRad)) / System.Math.PI) / 2 * n);
        return new TileKey(zoom, System.Math.Clamp(x, 0, n - 1), System.Math.Clamp(y, 0, n - 1));
    }

    /// <summary>North-west corner of a tile.</summary>
    public static GeoPoint TileOrigin(int zoom, int x, int y)
    {
        double n = 1 << zoom;
        double lon = x / n * 360 - 180;
        double lat = System.Math.Atan(System.Math.Sinh(System.Math.PI * (1 - 2 * y / n))) * 180 / System.Math.PI;
        return new GeoPoint(lat, lon);
    }

    public static (GeoPoint NorthWest, GeoPoint SouthEast) Bounds(TileKey k) =>
        (TileOrigin(k.Zoom, k.X, k.Y), TileOrigin(k.Zoom, k.X + 1, k.Y + 1));

    /// <summary>Bing-style quadkey (also used by some ArcGIS / Azure Maps layers).</summary>
    public static string QuadKey(TileKey k)
    {
        var chars = new char[k.Zoom];
        for (int i = k.Zoom; i > 0; i--)
        {
            int mask = 1 << (i - 1), d = 0;
            if ((k.X & mask) != 0) d++;
            if ((k.Y & mask) != 0) d += 2;
            chars[k.Zoom - i] = (char)('0' + d);
        }
        return new string(chars);
    }
}

/// <summary>A source of map imagery tiles (<c>osgEarth</c> image layer equivalent).</summary>
public abstract class TileSource
{
    public abstract string Name { get; }
    public abstract string GetUrl(TileKey key);
    public int MaxZoom { get; init; } = 19;
    public string Attribution { get; init; } = "";

    /// <summary>OpenStreetMap standard tiles. Respect the OSM tile usage policy (set a real User-Agent, cache tiles).</summary>
    public static TileSource OpenStreetMap() => new XyzTileSource("OpenStreetMap", "https://tile.openstreetmap.org/{z}/{x}/{y}.png") { Attribution = "© OpenStreetMap contributors" };

    /// <summary>ArcGIS REST MapServer tile endpoint (note the {z}/{y}/{x} order of ArcGIS).</summary>
    public static TileSource ArcGis(string mapServerUrl) => new XyzTileSource("ArcGIS", mapServerUrl.TrimEnd('/') + "/tile/{z}/{y}/{x}") { Attribution = "Esri" };

    /// <summary>OGC WMS 1.3.0 GetMap in EPSG:3857, one request per tile.</summary>
    public static TileSource Wms(string baseUrl, string layers, string format = "image/png", string styles = "") => new WmsTileSource(baseUrl, layers, format, styles);

    /// <summary>OGC WMTS KVP GetTile for a GoogleMapsCompatible / EPSG:3857 tile matrix set.</summary>
    public static TileSource Wmts(string baseUrl, string layer, string tileMatrixSet = "GoogleMapsCompatible", string format = "image/png", string style = "default") =>
        new WmtsTileSource(baseUrl, layer, tileMatrixSet, format, style);
}

public sealed class XyzTileSource(string name, string template) : TileSource
{
    public override string Name => name;
    public string Template => template;
    public override string GetUrl(TileKey k) => template
        .Replace("{z}", k.Zoom.ToString(CultureInfo.InvariantCulture))
        .Replace("{x}", k.X.ToString(CultureInfo.InvariantCulture))
        .Replace("{y}", k.Y.ToString(CultureInfo.InvariantCulture))
        .Replace("{q}", TileMath.QuadKey(k));
}

public sealed class WmsTileSource(string baseUrl, string layers, string format, string styles) : TileSource
{
    public override string Name => $"WMS:{layers}";

    public override string GetUrl(TileKey k)
    {
        const double origin = 20037508.342789244;
        double size = 2 * origin / (1 << k.Zoom);
        double minX = -origin + k.X * size, maxY = origin - k.Y * size;
        string bbox = string.Create(CultureInfo.InvariantCulture, $"{minX},{maxY - size},{minX + size},{maxY}");
        string sep = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{sep}SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS={Uri.EscapeDataString(layers)}&STYLES={Uri.EscapeDataString(styles)}" +
               $"&CRS=EPSG:3857&BBOX={bbox}&WIDTH=256&HEIGHT=256&FORMAT={Uri.EscapeDataString(format)}&TRANSPARENT=TRUE";
    }
}

public sealed class WmtsTileSource(string baseUrl, string layer, string matrixSet, string format, string style) : TileSource
{
    public override string Name => $"WMTS:{layer}";

    public override string GetUrl(TileKey k)
    {
        string sep = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{sep}SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={Uri.EscapeDataString(layer)}&STYLE={Uri.EscapeDataString(style)}" +
               $"&TILEMATRIXSET={Uri.EscapeDataString(matrixSet)}&TILEMATRIX={k.Zoom}&TILEROW={k.Y}&TILECOL={k.X}&FORMAT={Uri.EscapeDataString(format)}";
    }
}

/// <summary>
/// Downloads and caches map tiles (memory + optional disk cache) and builds textured ground tiles in local ENU metres.
/// PNG tiles decode with the built-in decoder; register an <see cref="IImageReader"/> for JPEG sources.
/// </summary>
public sealed class MapTileLoader(TileSource source, HttpClient http, string? diskCache = null)
{
    private readonly ConcurrentDictionary<TileKey, Image> _memory = new();

    public TileSource Source => source;

    public async Task<Image?> GetTileImageAsync(TileKey key, CancellationToken ct = default)
    {
        if (_memory.TryGetValue(key, out var cached)) return cached;
        string? file = diskCache is null ? null : Path.Combine(diskCache, source.Name.Replace(':', '_'), key.Zoom.ToString(), key.X.ToString(), $"{key.Y}.img");
        byte[] bytes;
        if (file is not null && File.Exists(file)) bytes = await File.ReadAllBytesAsync(file, ct);
        else
        {
            using var resp = await http.GetAsync(source.GetUrl(key), ct);
            if (!resp.IsSuccessStatusCode) return null;
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (file is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes, ct);
            }
        }
        var reader = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 'P' ? Registry.FindImageReader("png") : Registry.FindImageReader("jpg");
        if (reader is null) return null;
        var img = reader.ReadImage(new MemoryStream(bytes));
        _memory[key] = img;
        return img;
    }

    /// <summary>Builds a textured quad for a tile, positioned in ENU metres around <paramref name="origin"/>.</summary>
    public static Geometry BuildTileQuad(TileKey key, GeoPoint origin, Image? image, Ellipsoid? ellipsoid = null)
    {
        ellipsoid ??= Ellipsoid.Wgs84;
        var (nw, se) = TileMath.Bounds(key);
        var a = ellipsoid.ToEnu(new GeoPoint(se.Latitude, nw.Longitude, origin.Height), origin);
        var b = ellipsoid.ToEnu(new GeoPoint(se.Latitude, se.Longitude, origin.Height), origin);
        var c = ellipsoid.ToEnu(new GeoPoint(nw.Latitude, se.Longitude, origin.Height), origin);
        var d = ellipsoid.ToEnu(new GeoPoint(nw.Latitude, nw.Longitude, origin.Height), origin);
        var g = new Geometry
        {
            Name = $"tile {key}",
            Positions = [a, b, c, d],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
            Indices = [0, 1, 2, 0, 2, 3],
        };
        var ss = g.GetOrCreateStateSet();
        ss.Lighting = false;
        ss.Material = new Material { Diffuse = Vector4.One };
        if (image is not null) ss.Texture = new Texture2D(image) { Wrap = TextureWrap.ClampToEdge, Filter = TextureFilter.Linear };
        return g;
    }

    /// <summary>Loads a square of tiles around a location into a group (missing tiles get a grid placeholder).</summary>
    public async Task<Group> BuildAreaAsync(GeoPoint center, int zoom, int radiusTiles = 2, CancellationToken ct = default)
    {
        var group = new Group { Name = $"{source.Name} z{zoom}" };
        var c = TileMath.FromGeo(center.Latitude, center.Longitude, zoom);
        var tasks = new List<Task<(TileKey, Image?)>>();
        for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
            for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
            {
                var k = new TileKey(zoom, c.X + dx, c.Y + dy);
                tasks.Add(Task.Run(async () =>
                {
                    try { return (k, await GetTileImageAsync(k, ct)); }
                    catch (HttpRequestException) { return (k, (Image?)null); }
                }, ct));
            }
        var placeholder = Image.Checkerboard(64, 8, new Vector4(0.2f, 0.24f, 0.3f, 1), new Vector4(0.25f, 0.3f, 0.36f, 1));
        foreach (var (k, img) in await Task.WhenAll(tasks)) group.AddChild(BuildTileQuad(k, center, img ?? placeholder));
        return group;
    }
}

/// <summary>Globe geometry: a tessellated ellipsoid in ECEF (scaled), for space and satellite visualization.</summary>
public static class Globe
{
    /// <summary>Builds an ellipsoid mesh scaled by <paramref name="scale"/> (1e-6 = megametres), with lat/lon texture coordinates.</summary>
    public static Geometry Build(Ellipsoid? ellipsoid = null, float scale = 1e-6f, int segments = 96, int rings = 48, Image? texture = null)
    {
        ellipsoid ??= Ellipsoid.Wgs84;
        int vc = (segments + 1) * (rings + 1);
        var pos = new Vector3[vc];
        var nor = new Vector3[vc];
        var uv = new Vector2[vc];
        int k = 0;
        for (int r = 0; r <= rings; r++)
        {
            double lat = 90 - 180.0 * r / rings;
            for (int s = 0; s <= segments; s++)
            {
                double lon = -180 + 360.0 * s / segments;
                var e = ellipsoid.ToEcef(new GeoPoint(lat, lon));
                pos[k] = new Vector3((float)e.X, (float)e.Y, (float)e.Z) * scale;
                nor[k] = Vector3.Normalize(pos[k]);
                uv[k] = new Vector2((float)s / segments, (float)r / rings);
                k++;
            }
        }
        var g = new Geometry { Name = "Globe", Positions = pos, Normals = nor, TexCoords = uv, Indices = Shapes.GridIndices(segments, rings, flip: true) };
        var ss = g.GetOrCreateStateSet();
        ss.Material = new Material { Diffuse = Vector4.One, Specular = new Vector3(0.25f), Shininess = 24 };
        ss.Texture = new Texture2D(texture ?? ProceduralEarth(1024, 512)) { Wrap = TextureWrap.ClampToEdge };
        return g;
    }

    /// <summary>A stylised procedural Earth texture (oceans, continents from noise, ice caps), used when no imagery is available.</summary>
    public static Image ProceduralEarth(int width, int height, int seed = 7)
    {
        var noise = new Terrain.TerrainGenerator.ValueNoise(seed);
        var ocean = Color.FromHex("#1c3f6e");
        var deep = Color.FromHex("#0f2748");
        var land = Color.FromHex("#4f7a3a");
        var desert = Color.FromHex("#b59a62");
        var ice = Color.FromHex("#e8eef3");
        return Image.Generate(width, height, (u, v) =>
        {
            float lat = MathF.Abs(v - 0.5f) * 2; // 0 at equator, 1 at poles
            float n = 0, amp = 1, f = 3, norm = 0;
            for (int o = 0; o < 5; o++) { n += noise.Sample(u * f * 2, v * f) * amp; norm += amp; amp *= 0.5f; f *= 2; }
            n /= norm;
            if (lat > 0.86f) return ice;
            if (n < 0.52f) return Vector4.Lerp(deep, ocean, n / 0.52f);
            var c = MathF.Abs(lat - 0.3f) < 0.12f ? Vector4.Lerp(land, desert, 0.7f) : land;
            return Vector4.Lerp(c, ice, MathF.Max(0, lat - 0.7f) * 3);
        });
    }
}
