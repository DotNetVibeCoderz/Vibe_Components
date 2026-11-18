using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Survey;

public enum ObservationType { Distance, Direction, Angle, Azimuth, Coordinate, Baseline, HeightDifference }

/// <summary>One survey observation (angles in degrees, sigmas of angles in arc-seconds, lengths in metres).</summary>
public sealed record Observation(ObservationType Type, string From, string To, double Value, double Sigma, string? Via = null, string? Set = null, double Value2 = double.NaN, double Sigma2 = double.NaN);

/// <summary>Result of a least-squares adjustment.</summary>
public sealed class AdjustmentResult
{
    internal AdjustmentResult() { }

    /// <summary>id, e, n (or h), sigma_e, sigma_n, ellipse_a, ellipse_b, ellipse_azimuth (95 %), fixed.</summary>
    public DataFrame Stations { get; internal set; } = null!;
    /// <summary>type, from, to, observed, adjusted, residual, sigma, redundancy, std_residual, outlier.</summary>
    public DataFrame Observations { get; internal set; } = null!;
    public int DegreesOfFreedom { get; internal set; }
    /// <summary>A-posteriori variance factor σ̂0² = vᵀPv / r (≈ 1 when the a-priori sigmas are realistic).</summary>
    public double VarianceFactor { get; internal set; }
    public double ChiSquare { get; internal set; }
    public double ChiSquareLower { get; internal set; }
    public double ChiSquareUpper { get; internal set; }
    /// <summary>Global (χ²) test at the requested significance: true when σ̂0² is compatible with 1.</summary>
    public bool GlobalTestPassed => DegreesOfFreedom == 0 || (ChiSquare >= ChiSquareLower && ChiSquare <= ChiSquareUpper);
    public int Iterations { get; internal set; }
    public CoordinateReferenceSystem? Crs { get; internal set; }
    /// <summary>Observations whose standardised residual exceeds the data-snooping critical value (Baarda, 3.29).</summary>
    public IReadOnlyList<int> Outliers { get; internal set; } = [];

    public GeoDataFrame StationPoints() =>
        GeoDataFrame.FromGeometries(Enumerable.Range(0, Stations.RowCount).Select(i => (NtsGeometry?)Geo.Point(Stations["e"].GetDouble(i), Stations["n"].GetDouble(i))),
            Crs, Stations.Columns.Where(c => c.Name is not ("e" or "n")).ToArray());

    /// <summary>95 % confidence ellipses as polygons, exaggerated by <paramref name="scale"/> for plotting.</summary>
    public GeoDataFrame ErrorEllipses(double scale = 1000, int segments = 48)
    {
        var geoms = new List<NtsGeometry?>();
        for (int i = 0; i < Stations.RowCount; i++)
        {
            double a = Stations["ellipse_a"].GetDouble(i) * scale, b = Stations["ellipse_b"].GetDouble(i) * scale, t = Stations["ellipse_azimuth"].GetDouble(i) * Math.PI / 180;
            if (double.IsNaN(a) || a <= 0) { geoms.Add(null); continue; }
            double e0 = Stations["e"].GetDouble(i), n0 = Stations["n"].GetDouble(i);
            var pts = new (double, double)[segments];
            for (int k = 0; k < segments; k++)
            {
                double u = 2 * Math.PI * k / segments, x = a * Math.Cos(u), y = b * Math.Sin(u);
                // major axis along azimuth t (from north, clockwise)
                pts[k] = (e0 + x * Math.Sin(t) + y * Math.Cos(t), n0 + x * Math.Cos(t) - y * Math.Sin(t));
            }
            geoms.Add(Geo.Polygon(pts));
        }
        return GeoDataFrame.FromGeometries(geoms, Crs, Stations["id"]);
    }

    public override string ToString() =>
        $"Adjustment: {Stations.RowCount} stations, {Observations.RowCount} observations, dof {DegreesOfFreedom}, σ̂0² = {VarianceFactor:F3}, χ² test {(GlobalTestPassed ? "passed" : "FAILED")}, {Outliers.Count} flagged, {Iterations} iteration(s)";
}

/// <summary>
/// Least-squares adjustment of horizontal survey networks (Gauss–Newton on distances, directions with set orientation unknowns, angles,
/// azimuths, GNSS baselines and weighted coordinates) and of levelling networks (height differences). Reports adjusted coordinates,
/// standard deviations, 95 % error ellipses, redundancy numbers, Baarda data snooping and the χ² global test.
/// </summary>
public sealed class AdjustmentNetwork
{
    private const double Sec = Math.PI / 180 / 3600;
    private readonly Dictionary<string, (double E, double N, bool Fixed)> _stations = new();
    private readonly Dictionary<string, (double H, bool Fixed)> _heights = new();
    private readonly List<Observation> _obs = [];

    public CoordinateReferenceSystem? Crs { get; init; }

    public AdjustmentNetwork AddFixed(string id, double e, double n) { _stations[id] = (e, n, true); return this; }
    /// <summary>Unknown station with approximate coordinates.</summary>
    public AdjustmentNetwork AddStation(string id, double e, double n) { _stations[id] = (e, n, false); return this; }
    public AdjustmentNetwork AddBenchmark(string id, double h) { _heights[id] = (h, true); return this; }
    public AdjustmentNetwork AddHeightPoint(string id, double approximateH = 0) { _heights[id] = (approximateH, false); return this; }

    public AdjustmentNetwork Distance(string from, string to, double metres, double sigma) => Add(new(ObservationType.Distance, from, to, metres, sigma));
    /// <summary>Horizontal direction (degrees) observed at <paramref name="at"/>; directions of one setup share an orientation unknown (<paramref name="set"/>).</summary>
    public AdjustmentNetwork Direction(string at, string to, double degrees, double sigmaArcSec, string? set = null) => Add(new(ObservationType.Direction, at, to, degrees, sigmaArcSec, Set: set ?? at));
    /// <summary>Clockwise angle at <paramref name="at"/> from <paramref name="from"/> to <paramref name="to"/> (degrees).</summary>
    public AdjustmentNetwork Angle(string from, string at, string to, double degrees, double sigmaArcSec) => Add(new(ObservationType.Angle, at, to, degrees, sigmaArcSec, Via: from));
    public AdjustmentNetwork Azimuth(string from, string to, double degrees, double sigmaArcSec) => Add(new(ObservationType.Azimuth, from, to, degrees, sigmaArcSec));
    /// <summary>Weighted coordinate observation (e.g. a GNSS position or a soft constraint).</summary>
    public AdjustmentNetwork Coordinate(string id, double e, double n, double sigmaE, double sigmaN) => Add(new(ObservationType.Coordinate, id, id, e, sigmaE, Value2: n, Sigma2: sigmaN));
    /// <summary>GNSS baseline (ΔE, ΔN) between two stations.</summary>
    public AdjustmentNetwork Baseline(string from, string to, double dE, double dN, double sigmaE, double sigmaN) => Add(new(ObservationType.Baseline, from, to, dE, sigmaE, Value2: dN, Sigma2: sigmaN));
    /// <summary>Levelled height difference H(to) − H(from).</summary>
    public AdjustmentNetwork HeightDifference(string from, string to, double dh, double sigma) => Add(new(ObservationType.HeightDifference, from, to, dh, sigma));

    /// <summary>Levelling sigma from a precision in mm/√km and the line length in km.</summary>
    public static double LevelingSigma(double mmPerSqrtKm, double km) => mmPerSqrtKm / 1000 * Math.Sqrt(km);

    private AdjustmentNetwork Add(Observation o) { _obs.Add(o); return this; }
    public IReadOnlyList<Observation> Observations => _obs;

    // ================================================================== 2-D

    public AdjustmentResult Adjust2D(int maxIterations = 15, double tolerance = 1e-6, double significance = 0.05)
    {
        var obs = _obs.Where(o => o.Type != ObservationType.HeightDifference).ToList();
        var free = _stations.Where(s => !s.Value.Fixed).Select(s => s.Key).ToList();
        var idx = free.Select((k, i) => (k, i)).ToDictionary(t => t.k, t => 2 * t.i);
        var sets = obs.Where(o => o.Type == ObservationType.Direction).Select(o => o.Set!).Distinct().ToList();
        int nx = 2 * free.Count + sets.Count;
        var setIdx = sets.Select((s, i) => (s, i)).ToDictionary(t => t.s, t => 2 * free.Count + t.i);
        foreach (var o in obs)
            foreach (var id in new[] { o.From, o.To, o.Via }.Where(x => x is not null))
                if (!_stations.ContainsKey(id!)) throw new KeyNotFoundException($"Observation refers to unknown station '{id}'.");

        var e = _stations.ToDictionary(s => s.Key, s => s.Value.E);
        var n = _stations.ToDictionary(s => s.Key, s => s.Value.N);
        var orient = new double[sets.Count];
        // initial orientations from the first direction of each set
        for (int s = 0; s < sets.Count; s++)
        {
            var o = obs.First(x => x.Type == ObservationType.Direction && x.Set == sets[s]);
            orient[s] = Az(e, n, o.From, o.To) - o.Value * Math.PI / 180;
        }

        // rows: scalar observations, coordinate/baseline observations contribute two rows
        var rows = new List<(int Obs, int Comp)>();
        for (int i = 0; i < obs.Count; i++)
        {
            rows.Add((i, 0));
            if (obs[i].Type is ObservationType.Coordinate or ObservationType.Baseline) rows.Add((i, 1));
        }
        int m = rows.Count;
        if (m < nx) throw new InvalidOperationException($"Not enough observations ({m}) for {nx} unknowns.");
        var A = new double[m, nx]; var l = new double[m]; var p = new double[m];
        int iter = 0;
        double[] dx = new double[nx];
        double[,] q = new double[0, 0];
        for (; iter < maxIterations; iter++)
        {
            Array.Clear(A);
            for (int r = 0; r < m; r++) Linearize(obs[rows[r].Obs], rows[r].Comp, r, A, l, p, e, n, orient, idx, setIdx);
            q = SolveNormal(A, p, l, nx, out dx);
            for (int k = 0; k < free.Count; k++) { e[free[k]] += dx[2 * k]; n[free[k]] += dx[2 * k + 1]; }
            for (int s = 0; s < sets.Count; s++) orient[s] += dx[2 * free.Count + s];
            double maxCoord = 0;
            for (int k = 0; k < 2 * free.Count; k++) maxCoord = Math.Max(maxCoord, Math.Abs(dx[k]));
            if (maxCoord < tolerance) { iter++; break; }
        }
        // residuals at the solution
        Array.Clear(A);
        for (int r = 0; r < m; r++) Linearize(obs[rows[r].Obs], rows[r].Comp, r, A, l, p, e, n, orient, idx, setIdx);
        q = SolveNormal(A, p, l, nx, out dx);
        var v = new double[m];
        for (int r = 0; r < m; r++) { double s = 0; for (int k = 0; k < nx; k++) s += A[r, k] * dx[k]; v[r] = s - l[r]; }
        return Report(obs, rows, A, p, v, q, nx, significance, iter, free.Count, i =>
        {
            var (E, N, fixd) = _stations.ElementAt(i).Value;
            var id = _stations.ElementAt(i).Key;
            return (id, e[id], n[id], fixd, idx.TryGetValue(id, out var j) ? j : -1);
        }, _stations.Count, twoD: true, orient, sets, e, n);
    }

    private static double Az(Dictionary<string, double> e, Dictionary<string, double> n, string a, string b) => Math.Atan2(e[b] - e[a], n[b] - n[a]);
    private static double Wrap(double x) { x %= 2 * Math.PI; if (x > Math.PI) x -= 2 * Math.PI; if (x < -Math.PI) x += 2 * Math.PI; return x; }

    private static void Linearize(Observation o, int comp, int r, double[,] A, double[] l, double[] p, Dictionary<string, double> e, Dictionary<string, double> n,
        double[] orient, Dictionary<string, int> idx, Dictionary<string, int> setIdx)
    {
        void Set(string id, int c, double v) { if (idx.TryGetValue(id, out int j)) A[r, j + c] += v; }
        switch (o.Type)
        {
            case ObservationType.Distance:
            {
                double dE = e[o.To] - e[o.From], dN = n[o.To] - n[o.From], d = Math.Sqrt(dE * dE + dN * dN);
                Set(o.To, 0, dE / d); Set(o.To, 1, dN / d); Set(o.From, 0, -dE / d); Set(o.From, 1, -dN / d);
                l[r] = o.Value - d; p[r] = 1 / (o.Sigma * o.Sigma);
                break;
            }
            case ObservationType.Direction or ObservationType.Azimuth:
            {
                double dE = e[o.To] - e[o.From], dN = n[o.To] - n[o.From], d2 = dE * dE + dN * dN;
                Set(o.To, 0, dN / d2); Set(o.To, 1, -dE / d2); Set(o.From, 0, -dN / d2); Set(o.From, 1, dE / d2);
                double comp0 = Math.Atan2(dE, dN);
                if (o.Type == ObservationType.Direction) { int s = setIdx[o.Set!]; A[r, s] = -1; comp0 -= orient[s - (setIdx.Values.Min())]; }
                l[r] = Wrap(o.Value * Math.PI / 180 - comp0); p[r] = 1 / Math.Pow(o.Sigma * Sec, 2);
                break;
            }
            case ObservationType.Angle:
            {
                string at = o.From, back = o.Via!, fore = o.To;
                double dE1 = e[fore] - e[at], dN1 = n[fore] - n[at], d1 = dE1 * dE1 + dN1 * dN1;
                double dE2 = e[back] - e[at], dN2 = n[back] - n[at], d2 = dE2 * dE2 + dN2 * dN2;
                Set(fore, 0, dN1 / d1); Set(fore, 1, -dE1 / d1); Set(back, 0, -dN2 / d2); Set(back, 1, dE2 / d2);
                Set(at, 0, -dN1 / d1 + dN2 / d2); Set(at, 1, dE1 / d1 - dE2 / d2);
                double computed = Math.Atan2(dE1, dN1) - Math.Atan2(dE2, dN2);
                l[r] = Wrap(o.Value * Math.PI / 180 - computed); p[r] = 1 / Math.Pow(o.Sigma * Sec, 2);
                break;
            }
            case ObservationType.Coordinate:
                Set(o.From, comp, 1);
                l[r] = comp == 0 ? o.Value - e[o.From] : o.Value2 - n[o.From];
                p[r] = 1 / Math.Pow(comp == 0 ? o.Sigma : o.Sigma2, 2);
                break;
            case ObservationType.Baseline:
                Set(o.To, comp, 1); Set(o.From, comp, -1);
                l[r] = comp == 0 ? o.Value - (e[o.To] - e[o.From]) : o.Value2 - (n[o.To] - n[o.From]);
                p[r] = 1 / Math.Pow(comp == 0 ? o.Sigma : o.Sigma2, 2);
                break;
        }
    }

    // ================================================================== 1-D levelling

    public AdjustmentResult AdjustLeveling(double significance = 0.05)
    {
        var obs = _obs.Where(o => o.Type == ObservationType.HeightDifference).ToList();
        var free = _heights.Where(h => !h.Value.Fixed).Select(h => h.Key).ToList();
        var idx = free.Select((k, i) => (k, i)).ToDictionary(t => t.k, t => t.i);
        int m = obs.Count, nx = free.Count;
        if (m < nx) throw new InvalidOperationException("Not enough height differences.");
        var A = new double[m, nx]; var l = new double[m]; var p = new double[m];
        for (int r = 0; r < m; r++)
        {
            var o = obs[r];
            if (!_heights.ContainsKey(o.From) || !_heights.ContainsKey(o.To)) throw new KeyNotFoundException($"Unknown point in {o.From}→{o.To}.");
            if (idx.TryGetValue(o.To, out int a)) A[r, a] += 1;
            if (idx.TryGetValue(o.From, out int b)) A[r, b] -= 1;
            l[r] = o.Value - (_heights[o.To].H - _heights[o.From].H);
            p[r] = 1 / (o.Sigma * o.Sigma);
        }
        var q = SolveNormal(A, p, l, nx, out var dx);
        var v = new double[m];
        for (int r = 0; r < m; r++) { double s = 0; for (int k = 0; k < nx; k++) s += A[r, k] * dx[k]; v[r] = s - l[r]; }
        var h = _heights.ToDictionary(x => x.Key, x => x.Value.H + (idx.TryGetValue(x.Key, out int j) ? dx[j] : 0));
        var rows = Enumerable.Range(0, m).Select(i => (i, 0)).ToList();
        return Report(obs, rows, A, p, v, q, nx, significance, 1, free.Count, i =>
        {
            var id = _heights.ElementAt(i).Key;
            return (id, h[id], double.NaN, _heights[id].Fixed, idx.TryGetValue(id, out var k) ? k : -1);
        }, _heights.Count, twoD: false, [], [], null, null);
    }

    // ================================================================== shared

    /// <summary>Solves (AᵀPA) dx = AᵀPl by Cholesky; returns Qxx = (AᵀPA)⁻¹.</summary>
    private static double[,] SolveNormal(double[,] A, double[] p, double[] l, int nx, out double[] dx)
    {
        int m = l.Length;
        var N = new double[nx, nx]; var u = new double[nx];
        for (int r = 0; r < m; r++)
            for (int i = 0; i < nx; i++)
            {
                double ai = A[r, i];
                if (ai == 0) continue;
                u[i] += ai * p[r] * l[r];
                for (int j = 0; j < nx; j++) N[i, j] += ai * p[r] * A[r, j];
            }
        var L = Cholesky(N) ?? throw new InvalidOperationException("Normal matrix is singular: the network has a datum defect (fix at least one station and an orientation/scale) or a disconnected station.");
        dx = CholeskySolve(L, u);
        var Q = new double[nx, nx];
        var col = new double[nx];
        for (int k = 0; k < nx; k++)
        {
            Array.Clear(col); col[k] = 1;
            var x = CholeskySolve(L, col);
            for (int i = 0; i < nx; i++) Q[i, k] = x[i];
        }
        return Q;
    }

    private static double[,]? Cholesky(double[,] a)
    {
        int n = a.GetLength(0);
        var L = new double[n, n];
        double scale = 0;
        for (int i = 0; i < n; i++) scale = Math.Max(scale, Math.Abs(a[i, i]));
        for (int j = 0; j < n; j++)
        {
            double s = a[j, j];
            for (int k = 0; k < j; k++) s -= L[j, k] * L[j, k];
            if (s <= scale * 1e-13) return null;
            L[j, j] = Math.Sqrt(s);
            for (int i = j + 1; i < n; i++)
            {
                double t = a[i, j];
                for (int k = 0; k < j; k++) t -= L[i, k] * L[j, k];
                L[i, j] = t / L[j, j];
            }
        }
        return L;
    }

    private static double[] CholeskySolve(double[,] L, double[] b)
    {
        int n = b.Length;
        var y = new double[n];
        for (int i = 0; i < n; i++) { double s = b[i]; for (int k = 0; k < i; k++) s -= L[i, k] * y[k]; y[i] = s / L[i, i]; }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--) { double s = y[i]; for (int k = i + 1; k < n; k++) s -= L[k, i] * x[k]; x[i] = s / L[i, i]; }
        return x;
    }

    private AdjustmentResult Report(List<Observation> obs, List<(int Obs, int Comp)> rows, double[,] A, double[] p, double[] v, double[,] q, int nx,
        double significance, int iterations, int freeCount, Func<int, (string Id, double E, double N, bool Fixed, int Index)> station, int stationCount,
        bool twoD, double[] orient, List<string> sets, Dictionary<string, double>? e, Dictionary<string, double>? n)
    {
        int m = rows.Count, dof = m - nx;
        double vpv = 0;
        for (int r = 0; r < m; r++) vpv += v[r] * v[r] * p[r];
        double s0 = dof > 0 ? vpv / dof : 1;
        // redundancy numbers r_i = 1 − p_i a_i Q a_iᵀ
        var red = new double[m]; var w = new double[m];
        for (int r = 0; r < m; r++)
        {
            double qa = 0;
            for (int i = 0; i < nx; i++)
            {
                if (A[r, i] == 0) continue;
                for (int j = 0; j < nx; j++) qa += A[r, i] * q[i, j] * A[r, j];
            }
            red[r] = Math.Clamp(1 - p[r] * qa, 0, 1);
            w[r] = red[r] > 1e-9 ? v[r] * Math.Sqrt(p[r]) / Math.Sqrt(red[r]) : 0;
        }
        const double critical = 3.29; // Baarda, α0 = 0.001, β = 0.2
        var outliers = Enumerable.Range(0, m).Where(r => Math.Abs(w[r]) > critical).ToArray();

        var ids = new List<string>(); var ee = new List<double>(); var nn = new List<double>(); var se = new List<double>(); var sn = new List<double>();
        var ea = new List<double>(); var eb = new List<double>(); var et = new List<double>(); var fx = new List<bool>();
        double k95 = Math.Sqrt(Statistics.ChiSquareQuantile(0.95, twoD ? 2 : 1));
        for (int i = 0; i < stationCount; i++)
        {
            var (id, E, N, fixd, j) = station(i);
            ids.Add(id); ee.Add(E); nn.Add(N); fx.Add(fixd);
            if (j < 0) { se.Add(0); sn.Add(0); ea.Add(0); eb.Add(0); et.Add(0); continue; }
            if (!twoD)
            {
                double sh = Math.Sqrt(s0 * q[j, j]);
                se.Add(sh); sn.Add(double.NaN); ea.Add(k95 * sh); eb.Add(double.NaN); et.Add(double.NaN);
                continue;
            }
            double cee = s0 * q[j, j], cnn = s0 * q[j + 1, j + 1], cen = s0 * q[j, j + 1];
            se.Add(Math.Sqrt(cee)); sn.Add(Math.Sqrt(cnn));
            double mean = (cee + cnn) / 2, rad = Math.Sqrt((cee - cnn) * (cee - cnn) / 4 + cen * cen);
            double l1 = mean + rad, l2 = Math.Max(mean - rad, 0);
            double az = Math.Abs(cen) < 1e-30 ? (cee >= cnn ? 90 : 0) : Angles.Normalize(Math.Atan2(cen, l1 - cee) * 180 / Math.PI) % 180;
            ea.Add(k95 * Math.Sqrt(l1)); eb.Add(k95 * Math.Sqrt(l2)); et.Add(az);
        }
        var stations = new DataFrame(Column.Create("id", ids.ToArray()), Column.Create(twoD ? "e" : "h", ee.ToArray()),
            Column.Create("n", nn.ToArray()), Column.Create(twoD ? "sigma_e" : "sigma_h", se.ToArray()), Column.Create("sigma_n", sn.ToArray()),
            Column.Create("ellipse_a", ea.ToArray()), Column.Create("ellipse_b", eb.ToArray()), Column.Create("ellipse_azimuth", et.ToArray()), Column.Create("fixed", fx.ToArray()));
        if (!twoD) stations = stations.Drop("n", "sigma_n", "ellipse_b", "ellipse_azimuth").Rename("ellipse_a", "confidence_95");

        // observation table (angles reported in degrees, residuals in arc-seconds)
        var type = new string[m]; var from = new string[m]; var to = new string[m]; var observed = new double[m]; var adjusted = new double[m];
        var resid = new double[m]; var sigma = new double[m];
        for (int r = 0; r < m; r++)
        {
            var o = obs[rows[r].Obs];
            bool angle = o.Type is ObservationType.Direction or ObservationType.Azimuth or ObservationType.Angle;
            type[r] = o.Type.ToString().ToLowerInvariant() + (rows[r].Comp == 1 ? "_n" : o.Type is ObservationType.Coordinate or ObservationType.Baseline ? "_e" : "");
            from[r] = o.Type == ObservationType.Angle ? $"{o.Via}-{o.From}" : o.From;
            to[r] = o.To;
            double val = rows[r].Comp == 1 ? o.Value2 : o.Value;
            observed[r] = val;
            resid[r] = angle ? v[r] / Sec : v[r];
            adjusted[r] = angle ? Angles.Normalize(val + resid[r] / 3600) : val + v[r];
            sigma[r] = rows[r].Comp == 1 ? o.Sigma2 : o.Sigma;
        }
        var obsDf = new DataFrame(Column.Create("type", type), Column.Create("from", from), Column.Create("to", to), Column.Create("observed", observed),
            Column.Create("adjusted", adjusted), Column.Create("residual", resid), Column.Create("sigma", sigma), Column.Create("redundancy", red),
            Column.Create("std_residual", w), Column.Create("outlier", Enumerable.Range(0, m).Select(r => Math.Abs(w[r]) > critical).ToArray()));

        return new AdjustmentResult
        {
            Stations = stations, Observations = obsDf, DegreesOfFreedom = dof, VarianceFactor = s0, ChiSquare = vpv,
            ChiSquareLower = dof > 0 ? Statistics.ChiSquareQuantile(significance / 2, dof) : 0,
            ChiSquareUpper = dof > 0 ? Statistics.ChiSquareQuantile(1 - significance / 2, dof) : 0,
            Iterations = iterations, Crs = Crs, Outliers = outliers
        };
    }
}

/// <summary>χ² and normal quantiles for adjustment tests.</summary>
public static class Statistics
{
    /// <summary>Regularised lower incomplete gamma P(a, x).</summary>
    public static double GammaP(double a, double x)
    {
        if (x <= 0) return 0;
        double gln = LogGamma(a);
        if (x < a + 1)
        {
            double ap = a, sum = 1 / a, del = sum;
            for (int n = 0; n < 500; n++) { ap++; del *= x / ap; sum += del; if (Math.Abs(del) < Math.Abs(sum) * 1e-15) break; }
            return sum * Math.Exp(-x + a * Math.Log(x) - gln);
        }
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, h = d;
        for (int i = 1; i < 500; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < 1e-15) break;
        }
        return 1 - Math.Exp(-x + a * Math.Log(x) - gln) * h;
    }

    public static double LogGamma(double x)
    {
        double[] cof = [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5];
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        foreach (var c in cof) ser += c / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }

    public static double ChiSquareCdf(double x, double dof) => GammaP(dof / 2, x / 2);

    /// <summary>Quantile of the χ² distribution (bisection on the CDF).</summary>
    public static double ChiSquareQuantile(double probability, double dof)
    {
        double lo = 0, hi = Math.Max(10, dof * 10 + 100);
        for (int i = 0; i < 200; i++)
        {
            double mid = (lo + hi) / 2;
            if (ChiSquareCdf(mid, dof) < probability) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }
}
