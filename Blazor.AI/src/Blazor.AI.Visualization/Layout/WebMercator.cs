namespace Blazor.AI.Visualization;

/// <summary>Web Mercator (EPSG:3857) projection in "world pixels" at a zoom level, matching XYZ map tiles of 256 px.</summary>
public static class WebMercator
{
    public const int TileSize = 256;

    public static (double X, double Y) Project(double latitude, double longitude, double zoom)
    {
        var lat = Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180;
        var scale = TileSize * Math.Pow(2, zoom);
        var x = (longitude + 180) / 360 * scale;
        var y = (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * scale;
        return (x, y);
    }

    public static (double Latitude, double Longitude) Unproject(double x, double y, double zoom)
    {
        var scale = TileSize * Math.Pow(2, zoom);
        var longitude = x / scale * 360 - 180;
        var n = Math.PI - 2 * Math.PI * y / scale;
        var latitude = 180 / Math.PI * Math.Atan(Math.Sinh(n));
        return (latitude, longitude);
    }

    /// <summary>The highest integer zoom at which all points fit in the viewport.</summary>
    public static int FitZoom(IEnumerable<GeoPoint> points, double width, double height, int maxZoom = 12)
    {
        var list = points.ToList();
        if (list.Count < 2)
        {
            return list.Count == 1 ? 10 : 2;
        }

        for (var zoom = maxZoom; zoom >= 1; zoom--)
        {
            var projected = list.Select(p => Project(p.Latitude, p.Longitude, zoom)).ToList();
            if (projected.Max(p => p.X) - projected.Min(p => p.X) < width * 0.8 && projected.Max(p => p.Y) - projected.Min(p => p.Y) < height * 0.8)
            {
                return zoom;
            }
        }

        return 1;
    }
}
