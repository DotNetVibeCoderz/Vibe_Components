using System.Globalization;
using GDAL.Net.Vsi;

namespace GDAL.Net.Rendering;

/// <summary>Address of a Web Mercator (XYZ / "slippy map") tile.</summary>
public readonly record struct TileId(int Z, int X, int Y)
{
    public override string ToString() => $"{Z}/{X}/{Y}";
}

/// <summary>
/// Generates and serves XYZ (Google/OSM scheme) Web Mercator tiles from any raster — the managed equivalent of
/// gdal2tiles. The source is reprojected once into a lazy in-memory VRT; each tile reads only the pixels it needs.
/// </summary>
public sealed class TileGenerator : IDisposable
{
    public const double OriginShift = 20037508.342789244;

    private readonly Dataset _mercator;
    private readonly string _vrtPath;
    private readonly Lock _lock = new();

    /// <param name="source">Any georeferenced raster.</param>
    /// <param name="options">Render options (stretch, color ramp, ...).</param>
    /// <param name="tileSize">Tile size in pixels (256 or 512).</param>
    public TileGenerator(Dataset source, RenderOptions? options = null, int tileSize = 256)
    {
        Options = options ?? new RenderOptions();
        TileSize = tileSize;
        _vrtPath = VirtualFileSystem.NewMemoryPath(".vrt");
        _mercator = GdalUtils.Warp(source, _vrtPath, new WarpOptions
        {
            Format = "VRT",
            TargetSrs = "EPSG:3857",
            Resampling = ResampleAlgorithm.Bilinear,
            Multithread = false,
            AddAlpha = false,
        });
        Extent = RasterRenderer.FullView(_mercator);
        var pixel = Math.Abs(_mercator.GeoTransform.PixelWidth);
        NativeZoom = Math.Clamp((int)Math.Round(Math.Log2(2 * OriginShift / (tileSize * pixel))), 0, 24);
    }

    public RenderOptions Options { get; }
    public int TileSize { get; }

    /// <summary>Extent of the data in EPSG:3857 meters.</summary>
    public Envelope Extent { get; }

    /// <summary>Zoom level whose resolution best matches the source resolution.</summary>
    public int NativeZoom { get; }

    /// <summary>Bounds of a tile in EPSG:3857.</summary>
    public static Envelope TileBounds(TileId t)
    {
        var size = 2 * OriginShift / (1 << t.Z);
        var minX = -OriginShift + t.X * size;
        var maxY = OriginShift - t.Y * size;
        return new Envelope(minX, maxY - size, minX + size, maxY);
    }

    /// <summary>Tiles of a zoom level covering an EPSG:3857 extent.</summary>
    public static IEnumerable<TileId> TilesCovering(Envelope mercatorExtent, int zoom)
    {
        var n = 1 << zoom;
        var size = 2 * OriginShift / n;
        var x0 = Math.Clamp((int)Math.Floor((mercatorExtent.MinX + OriginShift) / size), 0, n - 1);
        var x1 = Math.Clamp((int)Math.Floor((mercatorExtent.MaxX + OriginShift) / size), 0, n - 1);
        var y0 = Math.Clamp((int)Math.Floor((OriginShift - mercatorExtent.MaxY) / size), 0, n - 1);
        var y1 = Math.Clamp((int)Math.Floor((OriginShift - mercatorExtent.MinY) / size), 0, n - 1);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++) yield return new TileId(zoom, x, y);
        }
    }

    /// <summary>Renders one tile (null when the tile does not intersect the data).</summary>
    public RgbaImage? RenderTile(TileId tile)
    {
        var bounds = TileBounds(tile);
        if (!bounds.Intersects(Extent)) return null;
        lock (_lock)
        {
            return RasterRenderer.Render(_mercator, Options, bounds, TileSize, TileSize);
        }
    }

    /// <summary>PNG bytes of a tile (null outside the data) — handy for a tile endpoint.</summary>
    public byte[]? GetTilePng(TileId tile) => RenderTile(tile)?.EncodePng(3);

    /// <summary>
    /// Writes {z}/{x}/{y}.png tiles for the zoom range into <paramref name="outputDirectory"/> plus a Leaflet preview page.
    /// Returns the number of tiles written (fully transparent tiles are skipped).
    /// </summary>
    public int Generate(string outputDirectory, int minZoom, int maxZoom, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var tiles = Enumerable.Range(minZoom, maxZoom - minZoom + 1).SelectMany(z => TilesCovering(Extent, z)).ToList();
        var written = 0;
        for (var i = 0; i < tiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = tiles[i];
            var img = RenderTile(t);
            if (img is not null && HasContent(img))
            {
                var dir = Path.Combine(outputDirectory, t.Z.ToString(CultureInfo.InvariantCulture), t.X.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{t.Y}.png"), img.EncodePng());
                written++;
            }
            progress?.Report(new GdalProgress((i + 1.0) / tiles.Count, t.ToString()));
        }
        WriteLeafletPreview(outputDirectory, minZoom, maxZoom);
        return written;
    }

    private static bool HasContent(RgbaImage img)
    {
        for (var i = 3; i < img.Pixels.Length; i += 4)
        {
            if (img.Pixels[i] != 0) return true;
        }
        return false;
    }

    private void WriteLeafletPreview(string dir, int minZoom, int maxZoom)
    {
        var sw = Mercator.ToLonLat(Extent.MinX, Extent.MinY);
        var ne = Mercator.ToLonLat(Extent.MaxX, Extent.MaxY);
        var html = $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>GDAL.Net tiles</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.min.css">
            <style>html,body,#map{height:100%;margin:0}</style></head>
            <body><div id="map"></div>
            <script src="https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.min.js"></script>
            <script>
            const map = L.map('map');
            L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {attribution: '&copy; OpenStreetMap'}).addTo(map);
            L.tileLayer('{z}/{x}/{y}.png', {minZoom: {{minZoom}}, maxNativeZoom: {{maxZoom}}, opacity: 0.85, attribution: 'GDAL.Net — Gravicode Studios'}).addTo(map);
            map.fitBounds([[{{F(sw.Lat)}}, {{F(sw.Lon)}}], [{{F(ne.Lat)}}, {{F(ne.Lon)}}]]);
            </script></body></html>
            """;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "leaflet.html"), html);
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _mercator.Dispose();
        VirtualFileSystem.Delete(_vrtPath);
    }
}

/// <summary>Spherical Web Mercator helpers.</summary>
public static class Mercator
{
    public static (double X, double Y) FromLonLat(double lon, double lat)
    {
        var x = lon * TileGenerator.OriginShift / 180;
        var y = Math.Log(Math.Tan((90 + Math.Clamp(lat, -85.05112878, 85.05112878)) * Math.PI / 360)) / (Math.PI / 180);
        return (x, y * TileGenerator.OriginShift / 180);
    }

    public static (double Lon, double Lat) ToLonLat(double x, double y)
    {
        var lon = x / TileGenerator.OriginShift * 180;
        var lat = y / TileGenerator.OriginShift * 180;
        lat = 180 / Math.PI * (2 * Math.Atan(Math.Exp(lat * Math.PI / 180)) - Math.PI / 2);
        return (lon, lat);
    }

    /// <summary>Tile containing a WGS84 position at a zoom level.</summary>
    public static TileId TileAt(double lon, double lat, int zoom)
    {
        var (x, y) = FromLonLat(lon, lat);
        var n = 1 << zoom;
        var size = 2 * TileGenerator.OriginShift / n;
        return new TileId(zoom, Math.Clamp((int)((x + TileGenerator.OriginShift) / size), 0, n - 1), Math.Clamp((int)((TileGenerator.OriginShift - y) / size), 0, n - 1));
    }
}
