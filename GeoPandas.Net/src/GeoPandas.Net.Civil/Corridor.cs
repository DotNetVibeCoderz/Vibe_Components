using GeoPandas.Net.Geometry;
using GeoPandas.Net.Raster;
using NetTopologySuite.Geometries;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Civil;

/// <summary>A template component: horizontal width (m) and cross slope (rise/run, negative = falling away from the centre line).</summary>
public sealed record TemplateSegment(double Width, double Slope, string Name = "");

/// <summary>
/// A typical cross-section: components to each side of the centre line, and daylight slopes to existing ground
/// (horizontal : vertical, e.g. 2 = 1V:2H).
/// </summary>
public sealed record CorridorTemplate
{
    public required IReadOnlyList<TemplateSegment> Left { get; init; }
    public required IReadOnlyList<TemplateSegment> Right { get; init; }
    public double CutSlope { get; init; } = 2;
    public double FillSlope { get; init; } = 3;
    /// <summary>Maximum horizontal search distance for the daylight line.</summary>
    public double MaxDaylight { get; init; } = 200;

    /// <summary>Two lanes and shoulders, symmetric: lane width and 2 % crown, shoulder at 4 %.</summary>
    public static CorridorTemplate TwoLaneRoad(double laneWidth = 3.5, double shoulderWidth = 1.5, double cutSlope = 2, double fillSlope = 3)
    {
        IReadOnlyList<TemplateSegment> side = [new(laneWidth, -0.02, "lane"), new(shoulderWidth, -0.04, "shoulder")];
        return new CorridorTemplate { Left = side, Right = side, CutSlope = cutSlope, FillSlope = fillSlope };
    }
}

/// <summary>A cross-section at a station: design and ground profiles as (offset, elevation), right offsets positive.</summary>
public sealed record CrossSection(double Station, IReadOnlyList<(double Offset, double Z)> Design, IReadOnlyList<(double Offset, double Z)> Ground,
    double CutArea, double FillArea, double LeftCatch, double RightCatch, bool LeftInCut, bool RightInCut);

/// <summary>
/// A road (or channel, embankment…) corridor: a template swept along horizontal and vertical alignments and daylighted to an existing
/// ground model. Gives cross-sections, cut/fill areas and volumes (average end area), a mass-haul diagram, feature lines, the footprint
/// and a design surface raster.
/// </summary>
public sealed class Corridor
{
    public Corridor(HorizontalAlignment horizontal, VerticalAlignment vertical, CorridorTemplate template, RasterGrid ground)
    {
        Horizontal = horizontal; Vertical = vertical; Template = template; Ground = ground;
    }

    public HorizontalAlignment Horizontal { get; }
    public VerticalAlignment Vertical { get; }
    public CorridorTemplate Template { get; }
    public RasterGrid Ground { get; }

    private double GroundAt(double station, double offset)
    {
        var (x, y, _) = Horizontal.PointAt(station, offset);
        return Ground.SampleBilinear(x, y);
    }

    /// <summary>The cross-section at a station.</summary>
    public CrossSection Section(double station)
    {
        double zc = Vertical.ElevationAt(station);
        var right = Side(station, zc, Template.Right, +1);
        var left = Side(station, zc, Template.Left, -1);
        var design = left.Points.AsEnumerable().Reverse().Concat(right.Points.Skip(1)).ToList();
        double lo = design[0].Offset, hi = design[^1].Offset;
        double step = Math.Max(Math.Min(Ground.CellSizeX, Ground.CellSizeY) / 2, 0.05);
        var offs = new SortedSet<double>(design.Select(p => p.Offset));
        for (double o = lo; o < hi; o += step) offs.Add(o);
        var ground = offs.Select(o => (o, GroundAt(station, o))).ToList();
        var (cut, fill) = Areas(design, ground);
        return new CrossSection(station, design, ground, cut, fill, lo, hi, left.InCut, right.InCut);
    }

    /// <summary>Template points from the centre line outwards, then the daylight point.</summary>
    private (List<(double Offset, double Z)> Points, bool InCut) Side(double station, double zc, IReadOnlyList<TemplateSegment> segs, int dir)
    {
        var pts = new List<(double Offset, double Z)> { (0, zc) };
        double o = 0, z = zc;
        foreach (var s in segs) { o += dir * s.Width; z += s.Slope * s.Width; pts.Add((o, z)); }
        double g = GroundAt(station, o);
        if (double.IsNaN(g)) return (pts, false);
        bool cut = g > z;
        double slope = cut ? 1 / Template.CutSlope : -1 / Template.FillSlope;   // dz per metre outward
        double F(double d) => z + slope * d - GroundAt(station, o + dir * d);    // design − ground along the daylight line
        double step = Math.Max(Math.Min(Ground.CellSizeX, Ground.CellSizeY) / 2, 0.05), prev = F(0);
        for (double d = step; d <= Template.MaxDaylight; d += step)
        {
            double cur = F(d);
            if (double.IsNaN(cur)) break;
            if (Math.Sign(cur) != Math.Sign(prev) || cur == 0)
            {
                double a = d - step, b = d;
                for (int it = 0; it < 60; it++)
                {
                    double m = (a + b) / 2, fm = F(m);
                    if (Math.Sign(fm) == Math.Sign(prev)) a = m; else b = m;
                }
                double dd = (a + b) / 2;
                pts.Add((o + dir * dd, z + slope * dd));
                return (pts, cut);
            }
            prev = cur;
        }
        return (pts, cut);
    }

    /// <summary>Cut and fill areas between two piecewise-linear profiles over the design's extent (exact, with crossings).</summary>
    internal static (double Cut, double Fill) Areas(IReadOnlyList<(double Offset, double Z)> design, IReadOnlyList<(double Offset, double Z)> ground)
    {
        double Interp(IReadOnlyList<(double Offset, double Z)> p, double o)
        {
            if (o <= p[0].Offset) return p[0].Z;
            for (int i = 1; i < p.Count; i++)
                if (o <= p[i].Offset) { double t = (o - p[i - 1].Offset) / (p[i].Offset - p[i - 1].Offset); return p[i - 1].Z + t * (p[i].Z - p[i - 1].Z); }
            return p[^1].Z;
        }
        var xs = design.Select(p => p.Offset).Concat(ground.Select(p => p.Offset)).Where(o => o >= design[0].Offset && o <= design[^1].Offset).Distinct().OrderBy(o => o).ToList();
        double cut = 0, fill = 0;
        for (int i = 1; i < xs.Count; i++)
        {
            double x0 = xs[i - 1], x1 = xs[i], w = x1 - x0;
            if (w <= 0) continue;
            double d0 = Interp(ground, x0) - Interp(design, x0), d1 = Interp(ground, x1) - Interp(design, x1);  // + = cut
            if (double.IsNaN(d0) || double.IsNaN(d1)) continue;
            if (d0 >= 0 && d1 >= 0) cut += (d0 + d1) / 2 * w;
            else if (d0 <= 0 && d1 <= 0) fill -= (d0 + d1) / 2 * w;
            else
            {
                double xc = w * d0 / (d0 - d1);     // crossing
                if (d0 > 0) { cut += d0 * xc / 2; fill -= d1 * (w - xc) / 2; }
                else { fill -= d0 * xc / 2; cut += d1 * (w - xc) / 2; }
            }
        }
        return (cut, fill);
    }

    /// <summary>Stations every <paramref name="interval"/> plus the alignment's element boundaries.</summary>
    public IReadOnlyList<double> SectionStations(double interval)
    {
        var set = new SortedSet<double>();
        for (double s = Horizontal.StartStation; s < Horizontal.EndStation; s += interval) set.Add(Math.Round(s, 6));
        set.Add(Horizontal.EndStation);
        foreach (var e in Horizontal.Elements) set.Add(Math.Round(e.StartStation, 6));
        return set.ToList();
    }

    public IReadOnlyList<CrossSection> Sections(double interval) => SectionStations(interval).AsParallel().AsOrdered().Select(Section).ToList();

    /// <summary>
    /// Earthwork by average end area: per station the cut and fill areas, volumes between consecutive sections, cumulative volumes and the
    /// mass-haul ordinate Σ(cut × <paramref name="cutFactor"/> − fill) (cutFactor &lt; 1 for shrinkage).
    /// </summary>
    public DataFrame Volumes(double interval, double cutFactor = 1.0)
    {
        var secs = Sections(interval);
        int n = secs.Count;
        var cutV = new double[n]; var fillV = new double[n]; var cumCut = new double[n]; var cumFill = new double[n]; var mass = new double[n];
        for (int i = 1; i < n; i++)
        {
            double l = secs[i].Station - secs[i - 1].Station;
            cutV[i] = (secs[i].CutArea + secs[i - 1].CutArea) / 2 * l;
            fillV[i] = (secs[i].FillArea + secs[i - 1].FillArea) / 2 * l;
            cumCut[i] = cumCut[i - 1] + cutV[i]; cumFill[i] = cumFill[i - 1] + fillV[i];
            mass[i] = mass[i - 1] + cutV[i] * cutFactor - fillV[i];
        }
        return new DataFrame(
            Column.Create("station", secs.Select(s => s.Station).ToArray()),
            Column.Create("label", secs.Select(s => HorizontalAlignment.FormatStation(s.Station)).ToArray()),
            Column.Create("design_z", secs.Select(s => Vertical.ElevationAt(s.Station)).ToArray()),
            Column.Create("ground_z", secs.Select(s => GroundAt(s.Station, 0)).ToArray()),
            Column.Create("cut_area", secs.Select(s => s.CutArea).ToArray()), Column.Create("fill_area", secs.Select(s => s.FillArea).ToArray()),
            Column.Create("cut_volume", cutV), Column.Create("fill_volume", fillV),
            Column.Create("cum_cut", cumCut), Column.Create("cum_fill", cumFill), Column.Create("mass_ordinate", mass));
    }

    /// <summary>3-D feature lines (Z = elevation): centre line, template edges and catch (daylight) lines on both sides.</summary>
    public GeoDataFrame FeatureLines(double interval)
    {
        var secs = Sections(interval);
        var names = new List<string>(); var lines = new List<NetTopologySuite.Geometries.Geometry?>();
        void Line(string name, Func<CrossSection, (double Offset, double Z)> pick)
        {
            var pts = secs.Select(s => { var (o, z) = pick(s); var (x, y, _) = Horizontal.PointAt(s.Station, o); return new CoordinateZ(x, y, z); }).ToArray();
            names.Add(name); lines.Add(Geo.Factory.CreateLineString(pts));
        }
        Line("centre", s => s.Design.First(p => p.Offset == 0));
        double leftEdge = -Template.Left.Sum(t => t.Width), rightEdge = Template.Right.Sum(t => t.Width);
        Line("left_edge", s => s.Design.First(p => Math.Abs(p.Offset - leftEdge) < 1e-9));
        Line("right_edge", s => s.Design.First(p => Math.Abs(p.Offset - rightEdge) < 1e-9));
        Line("left_catch", s => s.Design[0]);
        Line("right_catch", s => s.Design[^1]);
        return GeoDataFrame.FromGeometries(lines, Horizontal.Crs, Column.Create("line", names.ToArray()));
    }

    /// <summary>The corridor footprint (between the catch lines).</summary>
    public Polygon Footprint(double interval)
    {
        var secs = Sections(interval);
        var left = secs.Select(s => Horizontal.PointAt(s.Station, s.Design[0].Offset));
        var right = secs.Select(s => Horizontal.PointAt(s.Station, s.Design[^1].Offset)).Reverse();
        var ring = left.Concat(right).Select(p => (p.X, p.Y)).ToList();
        ring.Add(ring[0]);
        var poly = Geo.Polygon(ring.ToArray());
        return poly.IsValid ? poly : (Polygon)NetTopologySuite.Geometries.Utilities.GeometryFixer.Fix(poly) switch { Polygon p => p, var g => (Polygon)g.ConvexHull() };
    }

    /// <summary>
    /// The design surface on the ground raster's grid (NaN outside the corridor): sections are triangulated between consecutive stations
    /// and rasterised.
    /// </summary>
    public RasterGrid DesignSurface(double interval)
    {
        var secs = Sections(interval);
        int w = Ground.Width, h = Ground.Height;
        var band = new float[w * h];
        Array.Fill(band, float.NaN);
        // resample every section to the same normalised offsets so consecutive sections triangulate as a strip
        const int across = 64;
        (double X, double Y, double Z)[] Row(CrossSection s)
        {
            var res = new (double X, double Y, double Z)[across + 1];
            double lo = s.Design[0].Offset, hi = s.Design[^1].Offset;
            var breaks = s.Design;
            for (int k = 0; k <= across; k++)
            {
                double o = lo + (hi - lo) * k / across, z = breaks[^1].Z;
                for (int i = 1; i < breaks.Count; i++)
                    if (o <= breaks[i].Offset + 1e-12) { double t = (o - breaks[i - 1].Offset) / Math.Max(breaks[i].Offset - breaks[i - 1].Offset, 1e-12); z = breaks[i - 1].Z + t * (breaks[i].Z - breaks[i - 1].Z); break; }
                var (x, y, _) = Horizontal.PointAt(s.Station, o);
                res[k] = (x, y, z);
            }
            return res;
        }
        var rows = secs.Select(Row).ToList();
        for (int i = 1; i < rows.Count; i++)
            for (int k = 0; k < across; k++)
            {
                Triangle(band, rows[i - 1][k], rows[i - 1][k + 1], rows[i][k]);
                Triangle(band, rows[i - 1][k + 1], rows[i][k + 1], rows[i][k]);
            }
        return new RasterGrid(w, h, [band], Ground.Transform, Ground.Crs, float.NaN) { BandNames = ["design_z"] };
    }

    private void Triangle(float[] band, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
    {
        var t = Ground.Transform;
        var pa = t.WorldToPixel(a.X, a.Y); var pb = t.WorldToPixel(b.X, b.Y); var pc = t.WorldToPixel(c.X, c.Y);
        int c0 = Math.Max(0, (int)Math.Floor(Math.Min(pa.Col, Math.Min(pb.Col, pc.Col)))), c1 = Math.Min(Ground.Width - 1, (int)Math.Ceiling(Math.Max(pa.Col, Math.Max(pb.Col, pc.Col))));
        int r0 = Math.Max(0, (int)Math.Floor(Math.Min(pa.Row, Math.Min(pb.Row, pc.Row)))), r1 = Math.Min(Ground.Height - 1, (int)Math.Ceiling(Math.Max(pa.Row, Math.Max(pb.Row, pc.Row))));
        double den = (pb.Row - pc.Row) * (pa.Col - pc.Col) + (pc.Col - pb.Col) * (pa.Row - pc.Row);
        if (Math.Abs(den) < 1e-15) return;
        for (int r = r0; r <= r1; r++)
            for (int cc = c0; cc <= c1; cc++)
            {
                double x = cc + 0.5, y = r + 0.5;
                double l1 = ((pb.Row - pc.Row) * (x - pc.Col) + (pc.Col - pb.Col) * (y - pc.Row)) / den;
                double l2 = ((pc.Row - pa.Row) * (x - pc.Col) + (pa.Col - pc.Col) * (y - pc.Row)) / den;
                double l3 = 1 - l1 - l2;
                if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
                band[r * Ground.Width + cc] = (float)(l1 * a.Z + l2 * b.Z + l3 * c.Z);
            }
    }
}
