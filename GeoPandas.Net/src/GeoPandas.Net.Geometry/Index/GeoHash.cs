using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Geometry.Index;

/// <summary>GeoHash encoding/decoding (base-32, Niemeyer) — a hierarchical lon/lat cell index for bucketing and joins.</summary>
public static class GeoHash
{
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static string Encode(double lon, double lat, int precision = 9)
    {
        if (precision is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(precision));
        double minLat = -90, maxLat = 90, minLon = -180, maxLon = 180;
        Span<char> buf = stackalloc char[precision];
        bool even = true;
        int bit = 0, ch = 0, k = 0;
        while (k < precision)
        {
            if (even)
            {
                double mid = (minLon + maxLon) / 2;
                if (lon >= mid) { ch = (ch << 1) | 1; minLon = mid; } else { ch <<= 1; maxLon = mid; }
            }
            else
            {
                double mid = (minLat + maxLat) / 2;
                if (lat >= mid) { ch = (ch << 1) | 1; minLat = mid; } else { ch <<= 1; maxLat = mid; }
            }
            even = !even;
            if (++bit == 5) { buf[k++] = Base32[ch]; bit = 0; ch = 0; }
        }
        return new string(buf);
    }

    /// <summary>Decodes to the cell's bounding box (lon/lat).</summary>
    public static Envelope DecodeBounds(string hash)
    {
        double minLat = -90, maxLat = 90, minLon = -180, maxLon = 180;
        bool even = true;
        foreach (char c in hash.ToLowerInvariant())
        {
            int v = Base32.IndexOf(c);
            if (v < 0) throw new FormatException($"Invalid geohash character '{c}'.");
            for (int b = 4; b >= 0; b--)
            {
                int bitv = (v >> b) & 1;
                if (even) { double mid = (minLon + maxLon) / 2; if (bitv == 1) minLon = mid; else maxLon = mid; }
                else { double mid = (minLat + maxLat) / 2; if (bitv == 1) minLat = mid; else maxLat = mid; }
                even = !even;
            }
        }
        return new Envelope(minLon, maxLon, minLat, maxLat);
    }

    /// <summary>Decodes to the cell centre (lon, lat).</summary>
    public static (double Lon, double Lat) Decode(string hash)
    {
        var e = DecodeBounds(hash);
        return (e.Centre.X, e.Centre.Y);
    }

    /// <summary>Cell polygon for visualisation.</summary>
    public static Polygon ToPolygon(string hash)
    {
        var e = DecodeBounds(hash);
        return Geo.Box(e.MinX, e.MinY, e.MaxX, e.MaxY);
    }
}
