using GeoPandas.Net.Geometry;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Raster;

/// <summary>Raster ⇄ vector operations: zonal statistics, point sampling, rasterization, contouring.</summary>
public static class RasterVector
{
    /// <summary>Zonal statistics per polygon (count, min, max, mean, sum, std) appended as columns.</summary>
    public static GeoDataFrame ZonalStatistics(this Raster raster, GeoDataFrame zones, int band = 0, string prefix = "")
    {
        int n = zones.RowCount;
        var count = new long[n]; var min = new double[n]; var max = new double[n]; var mean = new double[n]; var sum = new double[n]; var std = new double[n];
        var data = raster.Bands[band];
        Parallel.For(0, n, z =>
        {
            min[z] = max[z] = mean[z] = sum[z] = std[z] = double.NaN;
            var g = zones.Geometry[z];
            if (g is null || g.IsEmpty) return;
            var mask = RasterizeMask(raster, g);
            double s = 0, sq = 0, lo = double.MaxValue, hi = double.MinValue; long k = 0;
            foreach (var i in mask)
            {
                var v = data[i];
                if (raster.IsNoData(v)) continue;
                s += v; sq += (double)v * v; lo = Math.Min(lo, v); hi = Math.Max(hi, v); k++;
            }
            count[z] = k;
            if (k == 0) return;
            sum[z] = s; mean[z] = s / k; min[z] = lo; max[z] = hi; std[z] = Math.Sqrt(Math.Max(0, sq / k - mean[z] * mean[z]));
        });
        return zones.WithColumn(Column.Create(prefix + "count", count)).WithColumn(Column.Create(prefix + "min", min))
            .WithColumn(Column.Create(prefix + "max", max)).WithColumn(Column.Create(prefix + "mean", mean))
            .WithColumn(Column.Create(prefix + "sum", sum)).WithColumn(Column.Create(prefix + "std", std));
    }

    /// <summary>Samples raster values at point (or representative point) locations.</summary>
    public static GeoDataFrame SamplePoints(this Raster raster, GeoDataFrame points, string column = "value", int band = 0, bool bilinear = false)
    {
        var vals = new double[points.RowCount];
        for (int i = 0; i < vals.Length; i++)
        {
            var g = points.Geometry[i];
            if (g is null) { vals[i] = double.NaN; continue; }
            var p = g is Point pt ? pt : g.InteriorPoint;
            vals[i] = bilinear ? raster.SampleBilinear(p.X, p.Y, band) : raster.Sample(p.X, p.Y, band);
        }
        return points.WithColumn(Column.Create(column, vals));
    }

    /// <summary>Cell indices whose centres fall inside a polygon (scanline fill, even-odd rule).</summary>
    public static List<int> RasterizeMask(Raster raster, NtsGeometry geometry)
    {
        var cells = new List<int>();
        int w = raster.Width, h = raster.Height;
        var t = raster.Transform;
        if (geometry is IPuntal)
        {
            foreach (var c in geometry.Coordinates)
            {
                var (col, row) = t.WorldToPixel(c.X, c.Y);
                if (col >= 0 && row >= 0 && col < w && row < h) cells.Add((int)row * w + (int)col);
            }
            return cells;
        }
        if (geometry is ILineal)
        {
            // sample along lines at half-cell spacing
            var seen = new HashSet<int>();
            double step = Math.Min(raster.CellSizeX, raster.CellSizeY) / 2;
            foreach (var part in GeometryOps.Explode(geometry))
            {
                var ls = (LineString)part;
                var idx = new NetTopologySuite.LinearReferencing.LengthIndexedLine(ls);
                for (double d = 0; d <= ls.Length; d += step)
                {
                    var c = idx.ExtractPoint(d);
                    var (col, row) = t.WorldToPixel(c.X, c.Y);
                    if (col >= 0 && row >= 0 && col < w && row < h && seen.Add((int)row * w + (int)col)) cells.Add((int)row * w + (int)col);
                }
            }
            return cells;
        }

        var rings = new List<Coordinate[]>();
        foreach (var part in GeometryOps.Explode(geometry))
        {
            if (part is not Polygon p) continue;
            rings.Add(p.ExteriorRing.Coordinates);
            foreach (var hole in p.InteriorRings) rings.Add(hole.Coordinates);
        }
        if (rings.Count == 0) return cells;
        var env = geometry.EnvelopeInternal;
        var (c0, r0) = t.WorldToPixel(env.MinX, env.MaxY);
        var (c1, r1) = t.WorldToPixel(env.MaxX, env.MinY);
        int rowMin = Math.Max(0, (int)Math.Floor(Math.Min(r0, r1))), rowMax = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(r0, r1)));
        var xs = new List<double>();
        for (int row = rowMin; row <= rowMax; row++)
        {
            var (_, y) = t.PixelToWorld(0.5, row + 0.5);
            xs.Clear();
            foreach (var ring in rings)
                for (int k = 0; k < ring.Length - 1; k++)
                {
                    var a = ring[k]; var b = ring[k + 1];
                    if ((a.Y > y) != (b.Y > y)) xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                var (ca, _) = t.WorldToPixel(xs[k], y);
                var (cb, _) = t.WorldToPixel(xs[k + 1], y);
                int from = Math.Max(0, (int)Math.Ceiling(Math.Min(ca, cb) - 0.5));
                int to = Math.Min(w - 1, (int)Math.Floor(Math.Max(ca, cb) - 0.5));
                for (int col = from; col <= to; col++) cells.Add(row * w + col);
            }
        }
        return cells;
    }

    /// <summary>Burns a GeoDataFrame into a new raster aligned to <paramref name="template"/> using a value column (or 1).</summary>
    public static Raster Rasterize(GeoDataFrame gdf, Raster template, string? valueColumn = null, float fill = float.NaN)
    {
        var data = new float[template.Width * template.Height];
        Array.Fill(data, fill);
        for (int i = 0; i < gdf.RowCount; i++)
        {
            var g = gdf.Geometry[i];
            if (g is null) continue;
            float v = valueColumn is null ? 1 : (float)gdf[valueColumn].GetDouble(i);
            foreach (var cell in RasterizeMask(template, g)) data[cell] = v;
        }
        return new Raster(template.Width, template.Height, [data], template.Transform, gdf.Crs ?? template.Crs, float.IsNaN(fill) ? null : fill);
    }

    /// <summary>Contour lines at the given interval (marching squares), returned as a GeoDataFrame with an <c>elevation</c> column.</summary>
    public static GeoDataFrame Contours(this Raster raster, double interval, double? baseValue = null, int band = 0)
    {
        var st = raster.Statistics(band);
        double start = baseValue ?? Math.Ceiling(st.Min / interval) * interval;
        var levels = new List<double>();
        for (double l = start; l <= st.Max; l += interval) levels.Add(l);
        var lines = new List<NtsGeometry?>(); var elev = new List<double>();
        foreach (var level in levels)
        {
            var segs = MarchingSquares(raster, band, level);
            if (segs.Count == 0) continue;
            var merger = new NetTopologySuite.Operation.Linemerge.LineMerger();
            foreach (var s in segs) merger.Add(s);
            var merged = merger.GetMergedLineStrings().Cast<LineString>().ToArray();
            lines.Add(merged.Length == 1 ? merged[0] : Geo.Factory.CreateMultiLineString(merged));
            elev.Add(level);
        }
        return GeoDataFrame.FromGeometries(lines, raster.Crs, Column.Create("elevation", elev.ToArray()));
    }

    private static List<LineString> MarchingSquares(Raster r, int band, double level)
    {
        int w = r.Width, h = r.Height;
        var z = r.Bands[band];
        var segs = new List<LineString>();
        var t = r.Transform;
        Coordinate P(double col, double row) { var (x, y) = t.PixelToWorld(col + 0.5, row + 0.5); return new Coordinate(x, y); }
        for (int row = 0; row < h - 1; row++)
            for (int col = 0; col < w - 1; col++)
            {
                double a = z[row * w + col], b = z[row * w + col + 1], c = z[(row + 1) * w + col + 1], d = z[(row + 1) * w + col];
                if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c) || double.IsNaN(d)) continue;
                int idx = (a >= level ? 8 : 0) | (b >= level ? 4 : 0) | (c >= level ? 2 : 0) | (d >= level ? 1 : 0);
                if (idx is 0 or 15) continue;
                double F(double p, double q) => Math.Abs(q - p) < 1e-12 ? 0.5 : (level - p) / (q - p);
                Coordinate top = P(col + F(a, b), row), right = P(col + 1, row + F(b, c)), bottom = P(col + F(d, c), row + 1), left = P(col, row + F(a, d));
                void S(Coordinate p, Coordinate q) => segs.Add(Geo.Factory.CreateLineString([p, q]));
                switch (idx)
                {
                    case 1 or 14: S(left, bottom); break;
                    case 2 or 13: S(bottom, right); break;
                    case 3 or 12: S(left, right); break;
                    case 4 or 11: S(top, right); break;
                    case 5: S(left, top); S(bottom, right); break;
                    case 6 or 9: S(top, bottom); break;
                    case 7 or 8: S(left, top); break;
                    case 10: S(left, bottom); S(top, right); break;
                }
            }
        return segs;
    }
}
