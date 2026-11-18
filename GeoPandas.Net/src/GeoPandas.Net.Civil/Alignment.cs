using System.Globalization;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Civil;

public enum AlignmentElementKind { Tangent, SpiralIn, Curve, SpiralOut }

/// <summary>
/// One element of a horizontal alignment. Azimuths are clockwise from north in radians; <see cref="Radius"/> is signed
/// (positive = curve to the right); spirals go from infinite radius to <see cref="Radius"/> (in) or back (out).
/// </summary>
public sealed record AlignmentElement(AlignmentElementKind Kind, double StartStation, double Length, double StartX, double StartY,
    double StartAzimuth, double Radius, double SpiralLength)
{
    public double EndStation => StartStation + Length;
}

/// <summary>Curve data at a point of intersection (PI): deflection, tangent length, external, middle ordinate, chord, spiral data.</summary>
public sealed record CurveReport(int Pi, double DeflectionDeg, double Radius, double SpiralLength, double TangentLength, double CurveLength,
    double TotalLength, double External, double MiddleOrdinate, double LongChord, double StationTs, double StationSc, double StationCs, double StationSt);

/// <summary>
/// A horizontal alignment built from points of intersection (PIs) with a circular radius and optional entry/exit clothoid
/// (Euler spiral) at each interior PI. Gives stationing, coordinates at station and offset, the inverse (station/offset of a point),
/// curve reports and line geometry.
/// </summary>
public sealed class HorizontalAlignment
{
    private readonly List<AlignmentElement> _elements = [];
    private readonly List<CurveReport> _curves = [];

    /// <param name="pis">Start point, PIs, end point.</param>
    /// <param name="radii">Radius at each interior PI (pis.Count − 2 values).</param>
    /// <param name="spiralLengths">Clothoid length at each interior PI (0 = simple circular curve); null = none.</param>
    /// <param name="startStation">Station of the start point.</param>
    /// <param name="crs">CRS of the coordinates.</param>
    public HorizontalAlignment(IReadOnlyList<(double X, double Y)> pis, IReadOnlyList<double> radii, IReadOnlyList<double>? spiralLengths = null,
        double startStation = 0, CoordinateReferenceSystem? crs = null)
    {
        if (pis.Count < 2) throw new ArgumentException("An alignment needs at least a start and an end point.");
        if (radii.Count != pis.Count - 2) throw new ArgumentException($"Expected {pis.Count - 2} radii (one per interior PI).");
        Crs = crs;
        StartStation = startStation;
        double sta = startStation;
        var cur = pis[0];
        for (int i = 1; i < pis.Count; i++)
        {
            var pi = pis[i];
            double azIn = Azimuth(pis[i - 1], pi);
            if (i == pis.Count - 1)
            {
                double len = Dist(cur, pi);
                if (len > 1e-9) _elements.Add(new(AlignmentElementKind.Tangent, sta, len, cur.X, cur.Y, azIn, 0, 0));
                sta += len;
                break;
            }
            double azOut = Azimuth(pi, pis[i + 1]);
            double delta = NormalizeAngle(azOut - azIn);              // + right, − left
            double r = radii[i - 1], ls = spiralLengths?[i - 1] ?? 0;
            if (Math.Abs(delta) < 1e-12 || r <= 0) { continue; }
            double ad = Math.Abs(delta), sign = Math.Sign(delta);
            double thetaS = ls / (2 * r);
            if (2 * thetaS > ad) throw new ArgumentException($"PI {i}: spirals of {ls} m are too long for a {ad * 180 / Math.PI:F3}° deflection at R = {r}.");
            var (xs, ys) = ls > 0 ? Clothoid(ls, r, ls) : (0, 0);
            double p = ls > 0 ? ys - r * (1 - Math.Cos(thetaS)) : 0;
            double k = ls > 0 ? xs - r * Math.Sin(thetaS) : 0;
            double ts = (r + p) * Math.Tan(ad / 2) + k;
            double lc = r * (ad - 2 * thetaS);
            // TS on the back tangent
            var tsPt = (X: pi.X - ts * Math.Sin(azIn), Y: pi.Y - ts * Math.Cos(azIn));
            double tanLen = Dist(cur, tsPt);
            if (Vector(cur, tsPt, azIn) < -1e-6) throw new ArgumentException($"PI {i}: curves overlap (tangent length {ts:F3} exceeds the available tangent).");
            if (tanLen > 1e-9) _elements.Add(new(AlignmentElementKind.Tangent, sta, tanLen, cur.X, cur.Y, azIn, 0, 0));
            sta += tanLen;
            double staTs = sta, x = tsPt.X, y = tsPt.Y, az = azIn;
            if (ls > 0)
            {
                _elements.Add(new(AlignmentElementKind.SpiralIn, sta, ls, x, y, az, sign * r, ls));
                (x, y, az) = PointOn(_elements[^1], ls);
                sta += ls;
            }
            double staSc = sta;
            _elements.Add(new(AlignmentElementKind.Curve, sta, lc, x, y, az, sign * r, 0));
            (x, y, az) = PointOn(_elements[^1], lc);
            sta += lc;
            double staCs = sta;
            if (ls > 0)
            {
                _elements.Add(new(AlignmentElementKind.SpiralOut, sta, ls, x, y, az, sign * r, ls));
                (x, y, az) = PointOn(_elements[^1], ls);
                sta += ls;
            }
            cur = (x, y);
            double ext = (r + p) / Math.Cos(ad / 2) - r;
            double mid = r * (1 - Math.Cos((ad - 2 * thetaS) / 2));
            double chord = 2 * r * Math.Sin((ad - 2 * thetaS) / 2);
            _curves.Add(new CurveReport(i, delta * 180 / Math.PI, r, ls, ts, lc, lc + 2 * ls, ext, mid, chord, staTs, staSc, staCs, sta));
        }
        EndStation = sta;
    }

    public CoordinateReferenceSystem? Crs { get; }
    public double StartStation { get; }
    public double EndStation { get; }
    public double Length => EndStation - StartStation;
    public IReadOnlyList<AlignmentElement> Elements => _elements;
    public IReadOnlyList<CurveReport> Curves => _curves;

    private static double Azimuth((double X, double Y) a, (double X, double Y) b) => Math.Atan2(b.X - a.X, b.Y - a.Y);
    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
    private static double Vector((double X, double Y) a, (double X, double Y) b, double az) => (b.X - a.X) * Math.Sin(az) + (b.Y - a.Y) * Math.Cos(az);
    /// <summary>Wraps an angle (radians) to (−π, π].</summary>
    public static double NormalizeAngle(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a <= -Math.PI) a += 2 * Math.PI; return a; }

    /// <summary>Local coordinates (along the initial tangent, offset to the right) of a clothoid at length s, for radius R reached at length Ls.</summary>
    public static (double X, double Y) Clothoid(double s, double radius, double spiralLength)
    {
        // θ(t) = t² / (2 R Ls); x = ∫cos θ, y = ∫sin θ — 20-point Gauss-Legendre on 8 panels (exact to ~1e-12 for practical spirals)
        double c = 1 / (2 * radius * spiralLength), x = 0, y = 0;
        const int panels = 8;
        double h = s / panels;
        for (int p = 0; p < panels; p++)
        {
            double a = p * h, mid = a + h / 2;
            for (int k = 0; k < GaussX.Length; k++)
            {
                double t = mid + h / 2 * GaussX[k], th = c * t * t;
                x += h / 2 * GaussW[k] * Math.Cos(th);
                y += h / 2 * GaussW[k] * Math.Sin(th);
            }
        }
        return (x, y);
    }

    private static readonly double[] GaussX =
    [
        -0.9931285991850949, -0.9639719272779138, -0.9122344282513259, -0.8391169718222188, -0.7463319064601508, -0.6360536807265150, -0.5108670019508271,
        -0.3737060887154195, -0.2277858511416451, -0.0765265211334973, 0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271,
        0.6360536807265150, 0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949
    ];
    private static readonly double[] GaussW =
    [
        0.0176140071391521, 0.0406014298003869, 0.0626720483341091, 0.0832767415767048, 0.1019301198172404, 0.1181945319615184, 0.1316886384491766,
        0.1420961093183820, 0.1491729864726037, 0.1527533871307258, 0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766,
        0.1181945319615184, 0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521
    ];

    /// <summary>Point and azimuth at distance d from the start of an element.</summary>
    private static (double X, double Y, double Az) PointOn(AlignmentElement e, double d)
    {
        double sx = Math.Sin(e.StartAzimuth), cx = Math.Cos(e.StartAzimuth);
        switch (e.Kind)
        {
            case AlignmentElementKind.Tangent:
                return (e.StartX + d * sx, e.StartY + d * cx, e.StartAzimuth);
            case AlignmentElementKind.Curve:
            {
                double r = e.Radius, phi = d / r;     // signed: right = positive
                // chord from the start: length 2r·sin(φ/2) at azimuth az0 + φ/2
                double chord = 2 * Math.Abs(r) * Math.Sin(Math.Abs(phi) / 2), a = e.StartAzimuth + phi / 2;
                return (e.StartX + chord * Math.Sin(a), e.StartY + chord * Math.Cos(a), e.StartAzimuth + phi);
            }
            case AlignmentElementKind.SpiralIn:
            {
                double r = Math.Abs(e.Radius), sign = Math.Sign(e.Radius);
                var (lx, ly) = Clothoid(d, r, e.SpiralLength);
                ly *= sign;
                // local x along az0, local y to the right
                return (e.StartX + lx * sx + ly * cx, e.StartY + lx * cx - ly * sx, e.StartAzimuth + sign * d * d / (2 * r * e.SpiralLength));
            }
            default:
            {
                // spiral out = the mirrored entry spiral run backwards from the ST
                double r = Math.Abs(e.Radius), sign = Math.Sign(e.Radius), ls = e.SpiralLength;
                double thetaS = ls / (2 * r), azEnd = e.StartAzimuth + sign * thetaS;
                var (ex, ey) = Clothoid(ls, r, ls);
                var (lx, ly) = Clothoid(ls - d, r, ls);
                // the ST seen from the CS, in the frame of the exit tangent
                double se = Math.Sin(azEnd), ce = Math.Cos(azEnd);
                double stX = e.StartX + ex * se - sign * ey * ce, stY = e.StartY + ex * ce + sign * ey * se;
                // walk back from the ST along the reversed spiral
                double px = stX - lx * se + sign * ly * ce, py = stY - lx * ce - sign * ly * se;
                double rem = ls - d;
                return (px, py, azEnd - sign * rem * rem / (2 * r * ls));
            }
        }
    }

    private AlignmentElement ElementAt(double station)
    {
        if (_elements.Count == 0) throw new InvalidOperationException("The alignment has no elements.");
        if (station <= StartStation) return _elements[0];
        foreach (var e in _elements) if (station <= e.EndStation + 1e-9) return e;
        return _elements[^1];
    }

    /// <summary>Coordinates and azimuth (radians, clockwise from north) at a station, shifted by an offset (positive = right).</summary>
    public (double X, double Y, double Azimuth) PointAt(double station, double offset = 0)
    {
        var e = ElementAt(station);
        var (x, y, az) = PointOn(e, Math.Clamp(station - e.StartStation, 0, e.Length));
        double extra = station - e.EndStation;
        if (extra > 0 && e == _elements[^1]) { x += extra * Math.Sin(az); y += extra * Math.Cos(az); }
        return (x + offset * Math.Cos(az), y - offset * Math.Sin(az), az);
    }

    /// <summary>Station and signed offset (right positive) of a point, by projection onto the alignment.</summary>
    public (double Station, double Offset) StationOffset(double x, double y)
    {
        // coarse search on a fine polyline, then Newton refinement on the tangent condition
        double best = StartStation, bd = double.MaxValue;
        double step = Math.Max(Length / 2000, 0.05);
        for (double s = StartStation; s <= EndStation + 1e-9; s += step)
        {
            var (px, py, _) = PointAt(s);
            double d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d < bd) { bd = d; best = s; }
        }
        double sta = best;
        for (int it = 0; it < 30; it++)
        {
            var (px, py, az) = PointAt(sta);
            double along = (x - px) * Math.Sin(az) + (y - py) * Math.Cos(az);
            sta = Math.Clamp(sta + along, StartStation, EndStation);
            if (Math.Abs(along) < 1e-10) break;
        }
        var (qx, qy, qaz) = PointAt(sta);
        double off = (x - qx) * Math.Cos(qaz) - (y - qy) * Math.Sin(qaz);
        return (sta, off);
    }

    /// <summary>The alignment as a line (curves densified to <paramref name="interval"/>), optionally offset.</summary>
    public LineString ToLineString(double interval = 1, double offset = 0)
    {
        var pts = new List<Coordinate>();
        foreach (var e in _elements)
        {
            int n = e.Kind == AlignmentElementKind.Tangent ? 1 : Math.Max(2, (int)Math.Ceiling(e.Length / interval));
            for (int k = pts.Count == 0 ? 0 : 1; k <= n; k++)
            {
                var (x, y, _) = PointAt(e.StartStation + e.Length * k / n, offset);
                pts.Add(new Coordinate(x, y));
            }
        }
        return Geo.LineString(pts);
    }

    /// <summary>Station ticks: points every <paramref name="interval"/> with the station label (e.g. "1+200").</summary>
    public GeoDataFrame Stations(double interval)
    {
        var sta = new List<double>();
        for (double s = Math.Ceiling(StartStation / interval) * interval; s <= EndStation + 1e-9; s += interval) sta.Add(s);
        return GeoDataFrame.FromGeometries(sta.Select(s => { var (x, y, _) = PointAt(s); return (NetTopologySuite.Geometries.Geometry?)Geo.Point(x, y); }), Crs,
            Column.Create("station", sta.ToArray()), Column.Create("label", sta.Select(FormatStation).ToArray()));
    }

    /// <summary>Elements as lines with kind, stations, length and radius.</summary>
    public GeoDataFrame ElementsFrame() => GeoDataFrame.FromGeometries(
        _elements.Select(e =>
        {
            int n = e.Kind == AlignmentElementKind.Tangent ? 1 : Math.Max(4, (int)(e.Length / 2));
            return (NetTopologySuite.Geometries.Geometry?)Geo.LineString(Enumerable.Range(0, n + 1).Select(k => { var (x, y, _) = PointOn(e, e.Length * k / n); return new Coordinate(x, y); }));
        }), Crs,
        Column.Create("kind", _elements.Select(e => e.Kind.ToString()).ToArray()),
        Column.Create("start_station", _elements.Select(e => e.StartStation).ToArray()),
        Column.Create("end_station", _elements.Select(e => e.EndStation).ToArray()),
        Column.Create("length", _elements.Select(e => e.Length).ToArray()),
        Column.Create("radius", _elements.Select(e => e.Radius).ToArray()));

    /// <summary>"km+mmm.mm" station format (1 234.5 → "1+234.50").</summary>
    public static string FormatStation(double station)
    {
        double a = Math.Abs(station);
        int km = (int)Math.Floor(a / 1000);
        return (station < 0 ? "-" : "") + km.ToString(CultureInfo.InvariantCulture) + "+" + (a - km * 1000).ToString("000.00", CultureInfo.InvariantCulture);
    }
}

/// <summary>A point of vertical intersection with a symmetric parabolic curve of length <see cref="CurveLength"/> (0 = angle point).</summary>
public sealed record Pvi(double Station, double Elevation, double CurveLength = 0);

/// <summary>
/// A vertical alignment (profile): grades between PVIs and symmetric parabolic vertical curves. Gives elevation and grade at any station,
/// K values and the high/low points of curves.
/// </summary>
public sealed class VerticalAlignment
{
    private readonly List<Pvi> _pvis;

    public VerticalAlignment(IEnumerable<Pvi> pvis)
    {
        _pvis = pvis.OrderBy(p => p.Station).ToList();
        if (_pvis.Count < 2) throw new ArgumentException("A profile needs at least two PVIs.");
        for (int i = 1; i < _pvis.Count - 1; i++)
        {
            double half = _pvis[i].CurveLength / 2;
            if (_pvis[i].Station - half < _pvis[i - 1].Station + _pvis[i - 1].CurveLength / 2 - 1e-9 || _pvis[i].Station + half > _pvis[i + 1].Station - _pvis[i + 1].CurveLength / 2 + 1e-9)
                throw new ArgumentException($"Vertical curve at station {_pvis[i].Station} overlaps its neighbours.");
        }
    }

    public IReadOnlyList<Pvi> Pvis => _pvis;
    public double Grade(int segment) => (_pvis[segment + 1].Elevation - _pvis[segment].Elevation) / (_pvis[segment + 1].Station - _pvis[segment].Station);

    /// <summary>Elevation at a station.</summary>
    public double ElevationAt(double station) => Evaluate(station).Elevation;

    /// <summary>Grade (rise/run) at a station.</summary>
    public double GradeAt(double station) => Evaluate(station).Grade;

    private (double Elevation, double Grade) Evaluate(double s)
    {
        for (int i = 1; i < _pvis.Count - 1; i++)
        {
            var v = _pvis[i];
            if (v.CurveLength <= 0) continue;
            double bvc = v.Station - v.CurveLength / 2, evc = v.Station + v.CurveLength / 2;
            if (s < bvc || s > evc) continue;
            double g1 = Grade(i - 1), g2 = Grade(i), x = s - bvc;
            double yBvc = v.Elevation - g1 * v.CurveLength / 2;
            return (yBvc + g1 * x + (g2 - g1) * x * x / (2 * v.CurveLength), g1 + (g2 - g1) * x / v.CurveLength);
        }
        int seg = 0;
        while (seg < _pvis.Count - 2 && s > _pvis[seg + 1].Station) seg++;
        double g = Grade(seg);
        return (_pvis[seg].Elevation + g * (s - _pvis[seg].Station), g);
    }

    /// <summary>Per vertical curve: station, length, grades in and out, K = L / |A| (A in %), crest/sag, high or low point.</summary>
    public DataFrame CurveTable()
    {
        var rows = Enumerable.Range(1, _pvis.Count - 2).Where(i => _pvis[i].CurveLength > 0).Select(i =>
        {
            var v = _pvis[i];
            double g1 = Grade(i - 1), g2 = Grade(i), a = (g2 - g1) * 100;
            double turnX = g1 * g2 < 0 ? -g1 * v.CurveLength / (g2 - g1) : double.NaN;
            double turnSta = v.Station - v.CurveLength / 2 + turnX;
            return (v.Station, v.CurveLength, G1: g1 * 100, G2: g2 * 100, K: v.CurveLength / Math.Abs(a), Type: a < 0 ? "crest" : "sag",
                TurnSta: turnSta, TurnElev: double.IsNaN(turnSta) ? double.NaN : ElevationAt(turnSta));
        }).ToList();
        return new DataFrame(
            Column.Create("pvi_station", rows.Select(r => r.Station).ToArray()), Column.Create("length", rows.Select(r => r.CurveLength).ToArray()),
            Column.Create("grade_in_pct", rows.Select(r => r.G1).ToArray()), Column.Create("grade_out_pct", rows.Select(r => r.G2).ToArray()),
            Column.Create("k", rows.Select(r => r.K).ToArray()), Column.Create("type", rows.Select(r => r.Type).ToArray()),
            Column.Create("turning_station", rows.Select(r => r.TurnSta).ToArray()), Column.Create("turning_elevation", rows.Select(r => r.TurnElev).ToArray()));
    }
}
