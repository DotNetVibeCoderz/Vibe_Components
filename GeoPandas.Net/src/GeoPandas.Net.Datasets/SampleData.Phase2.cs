using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.PointCloud;
using RasterGrid = GeoPandas.Net.Raster.Raster;
using GeoTransform = GeoPandas.Net.Raster.GeoTransform;

namespace GeoPandas.Net.Datasets;

public static partial class SampleData
{
    /// <summary>
    /// Synthetic classified airborne LiDAR tile (≈ 6 points/m²) over the city DEM: ground (2), buildings (6) with flat roofs,
    /// and tree crowns (3–5) with multiple returns. Deterministic for a seed; EPSG:32748.
    /// </summary>
    public static PointCloud.PointCloud LidarTile(double sizeX = 600, double sizeY = 400, double density = 6, int seed = 11)
    {
        var dem = Dem();
        var rng = new Random(seed);
        double x0 = CenterX - sizeX / 2 + 1500, y0 = CenterY - sizeY / 2 - 900;
        // buildings: axis-aligned blocks on a street grid
        var buildings = new List<(double X0, double Y0, double X1, double Y1, double H)>();
        for (double bx = x0 + 20; bx < x0 + sizeX - 40; bx += 55)
            for (double by = y0 + 20; by < y0 + sizeY - 40; by += 50)
                if (rng.NextDouble() < 0.55)
                {
                    double w = 18 + rng.NextDouble() * 22, h = 14 + rng.NextDouble() * 22;
                    buildings.Add((bx, by, bx + w, by + h, 6 + Math.Pow(rng.NextDouble(), 2) * 38));
                }
        // trees: clustered crowns in the gaps
        var trees = new List<(double X, double Y, double R, double H)>();
        for (int k = 0; k < (int)(sizeX * sizeY / 900); k++)
        {
            double tx = x0 + rng.NextDouble() * sizeX, ty = y0 + rng.NextDouble() * sizeY;
            if (buildings.Any(b => tx > b.X0 - 4 && tx < b.X1 + 4 && ty > b.Y0 - 4 && ty < b.Y1 + 4)) continue;
            trees.Add((tx, ty, 3 + rng.NextDouble() * 4, 8 + rng.NextDouble() * 14));
        }
        int n = (int)(sizeX * sizeY * density);
        var xs = new List<double>(n); var ys = new List<double>(n); var zs = new List<double>(n);
        var cls = new List<byte>(n); var ret = new List<byte>(n); var nret = new List<byte>(n); var inten = new List<ushort>(n);
        void Add(double x, double y, double z, byte c, byte r, byte nr, ushort i) { xs.Add(x); ys.Add(y); zs.Add(z); cls.Add(c); ret.Add(r); nret.Add(nr); inten.Add(i); }
        for (int i = 0; i < n; i++)
        {
            double x = x0 + rng.NextDouble() * sizeX, y = y0 + rng.NextDouble() * sizeY;
            double g = dem.SampleBilinear(x, y);
            if (double.IsNaN(g)) g = 30;
            double noise = (rng.NextDouble() - 0.5) * 0.06;
            var b = buildings.FirstOrDefault(b => x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1);
            if (b.H > 0) { Add(x, y, g + b.H + noise, LasClass.Building, 1, 1, (ushort)(1800 + rng.Next(400))); continue; }
            var t = trees.FirstOrDefault(t => (x - t.X) * (x - t.X) + (y - t.Y) * (y - t.Y) <= t.R * t.R);
            if (t.H > 0)
            {
                double d = Math.Sqrt((x - t.X) * (x - t.X) + (y - t.Y) * (y - t.Y)) / t.R;
                double crown = g + t.H * (1 - 0.35 * d * d);
                Add(x, y, crown + noise * 5, LasClass.HighVegetation, 1, 3, (ushort)(600 + rng.Next(500)));
                Add(x, y, g + t.H * (0.3 + 0.4 * rng.NextDouble()), LasClass.MediumVegetation, 2, 3, (ushort)(300 + rng.Next(300)));
                Add(x, y, g + noise, LasClass.Ground, 3, 3, (ushort)(150 + rng.Next(200)));
                continue;
            }
            Add(x, y, g + noise, LasClass.Ground, 1, 1, (ushort)(900 + rng.Next(600)));
        }
        var cnt = xs.Count;
        return new PointCloud.PointCloud([.. xs], [.. ys], [.. zs], Crs)
        {
            Classification = [.. cls], ReturnNumber = [.. ret], NumberOfReturns = [.. nret], Intensity = [.. inten],
            GpsTime = Enumerable.Range(0, cnt).Select(i => 3.2e8 + i * 1e-5).ToArray()
        };
    }

    /// <summary>
    /// Synthetic 2 m air temperature (K) over the Indonesian archipelago, 0.25° lon/lat grid, <paramref name="hours"/> hourly steps:
    /// a latitudinal gradient, land heating with a diurnal cycle and cooler highlands. Band names are ISO times.
    /// </summary>
    public static (RasterGrid Grid, DateTime[] Times) ClimateGrid(int hours = 6, int seed = 3)
    {
        const double west = 95, north = 6, cell = 0.25;
        int w = (int)((141 - west) / cell), h = (int)((north + 11) / cell);
        var rng = new Random(seed);
        var islands = new (double Lon, double Lat, double Rx, double Ry, double Elev)[]
        {
            (101.5, -0.5, 3.2, 4.6, 0.9), (110.0, -7.3, 4.6, 0.8, 0.8), (113.8, 0.6, 4.2, 3.4, 0.4), (120.4, -2.0, 1.6, 3.1, 0.7),
            (137.5, -4.3, 4.2, 2.0, 1.0), (115.2, -8.4, 0.6, 0.3, 0.6), (117.5, -8.7, 1.4, 0.4, 0.5), (124.5, -9.3, 1.4, 0.5, 0.4)
        };
        double Land(double lon, double lat)
        {
            double best = 0;
            foreach (var i in islands)
            {
                double d = Math.Pow((lon - i.Lon) / i.Rx, 2) + Math.Pow((lat - i.Lat) / i.Ry, 2);
                if (d < 1) best = Math.Max(best, (1 - d) * i.Elev);
            }
            return best;
        }
        var times = Enumerable.Range(0, hours).Select(k => new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc).AddHours(3 * k)).ToArray();
        var bands = new float[hours][];
        for (int k = 0; k < hours; k++)
        {
            var b = new float[w * h];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    double lon = west + (c + 0.5) * cell, lat = north - (r + 0.5) * cell;
                    double localHour = (times[k].Hour + lon / 15) % 24;
                    double sun = Math.Max(0, Math.Sin((localHour - 6) / 12 * Math.PI));
                    double land = Land(lon, lat);
                    double t = 300.5 - 0.06 * Math.Abs(lat + 3) + (land > 0 ? 6.5 * sun - 1.2 - 7 * land * land : 0.4 * sun) + (rng.NextDouble() - 0.5) * 0.3;
                    b[r * w + c] = (float)t;
                }
            bands[k] = b;
        }
        var grid = new RasterGrid(w, h, bands, GeoTransform.NorthUp(west, north, cell), CoordinateReferenceSystem.Wgs84, float.NaN)
        {
            BandNames = times.Select(t => t.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)).ToArray()
        };
        return (grid, times);
    }
}
