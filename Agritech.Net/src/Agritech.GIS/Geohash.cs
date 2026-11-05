using Agritech.Core.Geo;

namespace Agritech.GIS;

/// <summary>Geohash encoding for compact spatial keys (e.g. sensor bucketing, tile caching).</summary>
public static class Geohash
{
    private const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static string Encode(GeoCoordinate c, int precision = 9)
    {
        if (precision is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(precision));
        Span<char> buf = stackalloc char[precision];
        double latMin = -90, latMax = 90, lonMin = -180, lonMax = 180;
        bool even = true; int bit = 0, ch = 0, idx = 0;
        while (idx < precision)
        {
            if (even)
            {
                double mid = (lonMin + lonMax) / 2;
                if (c.Longitude >= mid) { ch |= 1 << (4 - bit); lonMin = mid; } else lonMax = mid;
            }
            else
            {
                double mid = (latMin + latMax) / 2;
                if (c.Latitude >= mid) { ch |= 1 << (4 - bit); latMin = mid; } else latMax = mid;
            }
            even = !even;
            if (++bit == 5) { buf[idx++] = Base32[ch]; bit = 0; ch = 0; }
        }
        return new string(buf);
    }

    public static GeoBounds DecodeBounds(string hash)
    {
        double latMin = -90, latMax = 90, lonMin = -180, lonMax = 180;
        bool even = true;
        foreach (char c in hash.ToLowerInvariant())
        {
            int cd = Base32.IndexOf(c);
            if (cd < 0) throw new FormatException($"Invalid geohash character '{c}'.");
            for (int mask = 16; mask > 0; mask >>= 1)
            {
                if (even)
                {
                    double mid = (lonMin + lonMax) / 2;
                    if ((cd & mask) != 0) lonMin = mid; else lonMax = mid;
                }
                else
                {
                    double mid = (latMin + latMax) / 2;
                    if ((cd & mask) != 0) latMin = mid; else latMax = mid;
                }
                even = !even;
            }
        }
        return new GeoBounds(latMin, lonMin, latMax, lonMax);
    }

    public static GeoCoordinate Decode(string hash) => DecodeBounds(hash).Center;

    /// <summary>The 8 neighbouring cells plus the cell itself.</summary>
    public static string[] Neighborhood(string hash)
    {
        var b = DecodeBounds(hash);
        double dLat = b.MaxLat - b.MinLat, dLon = b.MaxLon - b.MinLon;
        var c = b.Center;
        var res = new List<string>(9);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                double lat = Math.Clamp(c.Latitude + dy * dLat, -89.999999, 89.999999);
                double lon = (c.Longitude + dx * dLon + 540) % 360 - 180;
                res.Add(Encode(new GeoCoordinate(lat, lon), hash.Length));
            }
        return res.Distinct().ToArray();
    }
}
