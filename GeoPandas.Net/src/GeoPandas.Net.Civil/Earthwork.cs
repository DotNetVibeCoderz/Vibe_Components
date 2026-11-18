using GeoPandas.Net.Geometry;
using GeoPandas.Net.Raster;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Civil;

/// <summary>Cut/fill quantities between an existing and a design surface.</summary>
public sealed record EarthworkResult(double Cut, double Fill, double CutArea, double FillArea, RasterGrid CutFill)
{
    public double Net => Cut - Fill;
    /// <summary>Cut volume after applying a shrinkage/swell factor (e.g. 0.9) minus fill: positive = surplus material.</summary>
    public double Balance(double cutFactor) => Cut * cutFactor - Fill;
}

/// <summary>Grid (prism) earthwork between surfaces, and level pads.</summary>
public static class Earthwork
{
    /// <summary>
    /// Cut and fill volumes between two surfaces on the same grid. Cells where either surface is NaN/no-data are skipped; with a boundary,
    /// only cells whose centre lies inside count. The cut/fill raster is existing − design (positive = cut).
    /// </summary>
    public static EarthworkResult Volume(RasterGrid existing, RasterGrid design, NtsGeometry? boundary = null)
    {
        if (existing.Width != design.Width || existing.Height != design.Height) throw new ArgumentException("Surfaces must share the same grid.");
        double cellArea = existing.CellSizeX * existing.CellSizeY;
        var prepared = boundary is null ? null : PreparedGeometryFactory.Prepare(boundary);
        var diff = new float[existing.Width * existing.Height];
        double cut = 0, fill = 0; long cutCells = 0, fillCells = 0;
        var e = existing.Bands[0]; var d = design.Bands[0];
        for (int r = 0; r < existing.Height; r++)
            for (int c = 0; c < existing.Width; c++)
            {
                int i = r * existing.Width + c;
                float ze = e[i], zd = d[i];
                if (existing.IsNoData(ze) || design.IsNoData(zd)) { diff[i] = float.NaN; continue; }
                if (prepared is not null)
                {
                    var (x, y) = existing.Transform.PixelToWorld(c + 0.5, r + 0.5);
                    if (!prepared.Contains(Geo.Point(x, y))) { diff[i] = float.NaN; continue; }
                }
                double dz = ze - zd;
                diff[i] = (float)dz;
                if (dz > 0) { cut += dz * cellArea; cutCells++; }
                else if (dz < 0) { fill -= dz * cellArea; fillCells++; }
            }
        var raster = new RasterGrid(existing.Width, existing.Height, [diff], existing.Transform, existing.Crs, float.NaN) { BandNames = ["cut_fill"] };
        return new EarthworkResult(cut, fill, cutCells * cellArea, fillCells * cellArea, raster);
    }

    /// <summary>Cut and fill to a level pad at <paramref name="elevation"/> inside a polygon (no side slopes).</summary>
    public static EarthworkResult LevelPad(RasterGrid existing, Polygon pad, double elevation)
    {
        var flat = new float[existing.Width * existing.Height];
        Array.Fill(flat, (float)elevation);
        return Volume(existing, new RasterGrid(existing.Width, existing.Height, [flat], existing.Transform, existing.Crs), pad);
    }

    /// <summary>The pad elevation that balances cut × <paramref name="cutFactor"/> against fill (bisection).</summary>
    public static double BalancedPadElevation(RasterGrid existing, Polygon pad, double cutFactor = 1.0)
    {
        var s = existing.Statistics();
        double lo = s.Min, hi = s.Max;
        for (int it = 0; it < 60; it++)
        {
            double mid = (lo + hi) / 2;
            if (LevelPad(existing, pad, mid).Balance(cutFactor) > 0) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }
}

/// <summary>Parcel subdivision: splits a polygon into parts of equal or target area with parallel cut lines.</summary>
public static class Subdivision
{
    /// <summary>
    /// Splits <paramref name="parcel"/> into <paramref name="parts"/> pieces of equal area with parallel cuts. The cut lines are perpendicular
    /// to <paramref name="frontageAzimuthDeg"/> (azimuth of the frontage, degrees clockwise from north); by default the longest edge is the frontage.
    /// </summary>
    public static GeoDataFrame Split(Polygon parcel, int parts, double? frontageAzimuthDeg = null)
    {
        if (parts < 1) throw new ArgumentOutOfRangeException(nameof(parts));
        double total = parcel.Area;
        return SplitAt(parcel, Enumerable.Range(1, parts - 1).Select(k => total * k / parts).ToList(), frontageAzimuthDeg);
    }

    /// <summary>
    /// Splits off lots of <paramref name="targetArea"/> along the frontage; the remainder (smaller than <paramref name="minArea"/>, default half a lot)
    /// is merged into the last lot.
    /// </summary>
    public static GeoDataFrame SplitByArea(Polygon parcel, double targetArea, double? frontageAzimuthDeg = null, double? minArea = null)
    {
        double total = parcel.Area;
        int full = (int)Math.Floor(total / targetArea);
        double rem = total - full * targetArea;
        if (full == 0) return SplitAt(parcel, [], frontageAzimuthDeg);
        if (rem < (minArea ?? targetArea / 2)) full--;            // remainder joins the last lot
        return SplitAt(parcel, Enumerable.Range(1, full).Select(k => targetArea * k).ToList(), frontageAzimuthDeg);
    }

    /// <summary>Frontage direction: the longest exterior edge.</summary>
    private static double LongestEdgeAzimuth(Polygon p)
    {
        var cs = p.ExteriorRing.Coordinates;
        double best = 0, az = 0;
        for (int i = 1; i < cs.Length; i++)
        {
            double dx = cs[i].X - cs[i - 1].X, dy = cs[i].Y - cs[i - 1].Y, l = Math.Sqrt(dx * dx + dy * dy);
            if (l > best) { best = l; az = Math.Atan2(dx, dy); }
        }
        return az;
    }

    private static GeoDataFrame SplitAt(Polygon parcel, IReadOnlyList<double> cumulativeAreas, double? frontageAzimuthDeg)
    {
        double az = frontageAzimuthDeg is { } a ? a * Math.PI / 180 : LongestEdgeAzimuth(parcel);
        // sweep along the frontage direction u; cut lines are perpendicular to it
        double ux = Math.Sin(az), uy = Math.Cos(az), vx = uy, vy = -ux;
        var cs = parcel.ExteriorRing.Coordinates;
        double tMin = cs.Min(c => c.X * ux + c.Y * uy), tMax = cs.Max(c => c.X * ux + c.Y * uy);
        double vMin = cs.Min(c => c.X * vx + c.Y * vy) - 1, vMax = cs.Max(c => c.X * vx + c.Y * vy) + 1;
        Polygon Slab(double t0, double t1)
        {
            (double, double) P(double t, double v) => (t * ux + v * vx, t * uy + v * vy);
            return Geo.Polygon([P(t0, vMin), P(t1, vMin), P(t1, vMax), P(t0, vMax), P(t0, vMin)]);
        }
        double AreaBelow(double t) => parcel.Intersection(Slab(tMin - 1, t)).Area;
        var cuts = new List<double> { tMin - 1 };
        foreach (var target in cumulativeAreas)
        {
            double lo = cuts[^1], hi = tMax + 1;
            for (int it = 0; it < 80; it++)
            {
                double mid = (lo + hi) / 2;
                if (AreaBelow(mid) < target) lo = mid; else hi = mid;
            }
            cuts.Add((lo + hi) / 2);
        }
        cuts.Add(tMax + 1);
        var geoms = new List<NtsGeometry?>();
        for (int i = 1; i < cuts.Count; i++) geoms.Add(parcel.Intersection(Slab(cuts[i - 1], cuts[i])));
        return GeoDataFrame.FromGeometries(geoms, null,
            Column.Create("lot", Enumerable.Range(1, geoms.Count).Select(i => (long)i).ToArray()),
            Column.Create("area", geoms.Select(g => g!.Area).ToArray()),
            Column.Create("frontage_m", geoms.Select(g => FrontageLength(g!, parcel, az)).ToArray()));
    }

    /// <summary>Length of the lot's boundary lying on the parcel's frontage edge.</summary>
    private static double FrontageLength(NtsGeometry lot, Polygon parcel, double az)
    {
        var cs = parcel.ExteriorRing.Coordinates;
        for (int i = 1; i < cs.Length; i++)
        {
            double dx = cs[i].X - cs[i - 1].X, dy = cs[i].Y - cs[i - 1].Y;
            if (Math.Abs(HorizontalAlignment.NormalizeAngle(Math.Atan2(dx, dy) - az)) < 1e-9 || Math.Abs(HorizontalAlignment.NormalizeAngle(Math.Atan2(dx, dy) - az - Math.PI)) < 1e-9)
            {
                var edge = Geo.LineString((cs[i - 1].X, cs[i - 1].Y), (cs[i].X, cs[i].Y));
                return edge.Intersection(lot).Length;
            }
        }
        return double.NaN;
    }
}
