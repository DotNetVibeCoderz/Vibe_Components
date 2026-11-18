using System.Globalization;

namespace GeoPandas.Net.Network.Utility;

/// <summary>Options of the steady-state hydraulic solver.</summary>
public sealed record HydraulicOptions
{
    /// <summary>Convergence: Σ|Δq| / Σ|q| below this (null = the network's <see cref="WaterNetwork.Accuracy"/>).</summary>
    public double? Accuracy { get; init; }
    public int MaxIterations { get; init; } = 200;
    /// <summary>Pattern step used for demands (0 = the first multiplier, as EPANET at time 0).</summary>
    public int PatternStep { get; init; }
    /// <summary>Apply simple controls (<c>IF NODE … ABOVE/BELOW</c> on initial tank levels, <c>AT TIME</c> at time 0) before solving.</summary>
    public bool ApplyControls { get; init; } = true;
    /// <summary>Links forced closed (e.g. pipes isolated for a repair).</summary>
    public IReadOnlyCollection<string>? ClosedLinks { get; init; }
}

/// <summary>Result of a steady-state solve. Arrays follow <see cref="WaterNetwork.Nodes"/> and <see cref="WaterNetwork.Links"/>; SI units.</summary>
public sealed class HydraulicResult
{
    internal HydraulicResult(WaterNetwork net, double[] head, double[] demand, double[] flow, double[] headloss, bool[] open, bool[] disconnected, int iterations, double relChange)
    {
        Network = net; Head = head; Demand = demand; Flow = flow; HeadLoss = headloss; LinkOpen = open; Disconnected = disconnected;
        Iterations = iterations; RelativeChange = relChange;
    }

    public WaterNetwork Network { get; }
    /// <summary>Hydraulic head (m).</summary>
    public double[] Head { get; }
    /// <summary>Pressure head (m) = head − elevation (tank: water level).</summary>
    public double[] Pressure => Network.Nodes.Select((n, i) => n.Kind == WaterNodeKind.Reservoir ? 0 : Head[i] - n.Elevation).ToArray();
    /// <summary>Delivered demand (m³/s); for reservoirs and tanks the net outflow (negative = supplying the network).</summary>
    public double[] Demand { get; }
    /// <summary>Link flow (m³/s), positive From → To.</summary>
    public double[] Flow { get; }
    /// <summary>Head loss over the link (m), negative for a pump's head gain.</summary>
    public double[] HeadLoss { get; }
    public bool[] LinkOpen { get; }
    /// <summary>Junctions cut off from every reservoir and tank (head is NaN).</summary>
    public bool[] Disconnected { get; }
    public int Iterations { get; }
    public double RelativeChange { get; }
    public double[] Velocity => Network.Links.Select((l, i) => l.Diameter > 0 ? Math.Abs(Flow[i]) / (Math.PI * l.Diameter * l.Diameter / 4) : 0).ToArray();

    /// <summary>Nodes with head, pressure and demand (SI).</summary>
    public GeoDataFrame NodesFrame()
    {
        var f = Network.NodesFrame();
        return f.WithColumn(Column.Create("head", Head)).WithColumn(Column.Create("pressure", Pressure))
            .WithColumn(Column.Create("demand_out", Demand)).WithColumn(Column.Create("disconnected", Disconnected));
    }

    /// <summary>Links with flow, velocity, head loss and status (SI).</summary>
    public GeoDataFrame LinksFrame()
    {
        var f = Network.LinksFrame();
        return f.WithColumn(Column.Create("flow", Flow)).WithColumn(Column.Create("velocity", Velocity))
            .WithColumn(Column.Create("headloss", HeadLoss)).WithColumn(Column.Create("open", LinkOpen));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"Hydraulics: {Iterations} iterations (relative change {RelativeChange:E2}), pressure {Pressure.Where((p, i) => Network.Nodes[i].Kind == WaterNodeKind.Junction && !Disconnected[i]).DefaultIfEmpty().Min():F2} … {Pressure.Where((p, i) => Network.Nodes[i].Kind == WaterNodeKind.Junction && !Disconnected[i]).DefaultIfEmpty().Max():F2} m, {Disconnected.Count(d => d)} disconnected");
}

/// <summary>
/// Demand-driven steady-state hydraulics by the global gradient algorithm of Todini &amp; Pilati (1988), the method of EPANET 2.
/// Supports pipes (Hazen-Williams, Darcy-Weisbach, Chezy-Manning; minor losses; check valves), pumps (single-point and three-point
/// power curves, multi-point curves, constant power, speed) and open/closed/throttle-control valves. Reservoirs and tanks are fixed heads
/// (a snapshot). Pressure-regulating valves are not modelled yet.
/// </summary>
public static class Hydraulics
{
    // EPANET's US-unit coefficients converted to SI (m, m³/s)
    private const double Cfs = 0.028316846592, Ft = 0.3048;
    private static readonly double HwK = 4.727 * Math.Pow(Cfs, -1.852) * Math.Pow(Ft, 4.871);   // h = HwK·L·q^1.852 / (C^1.852 d^4.871)
    private static readonly double MinorK = 0.02517 * Ft / (Cfs * Cfs) * Math.Pow(Ft, 4);       // h = MinorK·K·q² / d⁴
    private const double Viscosity = 1.1e-5 * Ft * Ft;   // EPANET default kinematic viscosity, 1.1e-5 ft²/s
    private const double RqTol = 1e-7 * Ft / Cfs;        // EPANET RQTOL: lower bound of dh/dq, 1e-7 ft/cfs

    public static HydraulicResult Solve(WaterNetwork net, HydraulicOptions? options = null)
    {
        options ??= new HydraulicOptions();
        var ix = net.NodeIndex();
        int nn = net.Nodes.Count, nl = net.Links.Count;
        var from = net.Links.Select(l => ix[l.From]).ToArray();
        var to = net.Links.Select(l => ix[l.To]).ToArray();
        bool[] fixedHead = net.Nodes.Select(n => n.Kind != WaterNodeKind.Junction).ToArray();
        var head = new double[nn];
        for (int i = 0; i < nn; i++)
        {
            var n = net.Nodes[i];
            head[i] = n.Kind switch
            {
                WaterNodeKind.Reservoir => n.Elevation * net.PatternValue(n.HeadPattern ?? "\0", options.PatternStep),
                WaterNodeKind.Tank => n.Elevation + n.InitialLevel,
                _ => n.Elevation
            };
        }
        var demand = net.Nodes.Select(n => n.Kind == WaterNodeKind.Junction ? net.DemandAt(n, options.PatternStep) : 0).ToArray();

        // initial status
        var open = net.Links.Select(l => l.Status == LinkStatus.Open).ToArray();
        var linkIx = net.Links.Select((l, i) => (l.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.OrdinalIgnoreCase);
        if (options.ApplyControls)
            foreach (var c in net.Controls)
            {
                if (!linkIx.TryGetValue(c.Link, out var li)) continue;
                bool fire = false;
                if (c.AtTimeHours is { } h) fire = h == 0;
                else if (c.Node is not null && ix.TryGetValue(c.Node, out var ni))
                {
                    var node = net.Nodes[ni];
                    double v = node.Kind == WaterNodeKind.Tank ? node.InitialLevel : node.Kind == WaterNodeKind.Junction ? double.NaN : node.Elevation;
                    if (!double.IsNaN(v)) fire = c.Above ? v > c.Value : v < c.Value;
                }
                if (!fire) continue;
                if (c.Action == "OPEN") open[li] = true;
                else if (c.Action == "CLOSED") open[li] = false;
            }
        if (options.ClosedLinks is { } closed) foreach (var id in closed) if (linkIx.TryGetValue(id, out var li)) open[li] = false;
        foreach (var (l, i) in net.Links.Select((l, i) => (l, i)))
            if (l.Kind == WaterLinkKind.Valve && l.ValveType is not ("TCV" or null) && open[i] && l.Status != LinkStatus.Closed)
                throw new NotSupportedException($"Valve {l.Id} is a {l.ValveType}; only TCVs and closed valves are supported by this solver.");

        // pump curves → (h0, r, n) or a multi-point table
        var pumps = new PumpModel?[nl];
        for (int i = 0; i < nl; i++)
        {
            var l = net.Links[i];
            if (l.Kind != WaterLinkKind.Pump) continue;
            pumps[i] = l.PumpCurve is not null ? PumpModel.FromCurve(net.Curves[l.PumpCurve]) : PumpModel.ConstantPower(l.PumpPower ?? throw new InvalidDataException($"Pump {l.Id} has neither a curve nor a power."));
        }

        // initial flows: 1 ft/s in pipes, design flow in pumps (EPANET)
        var q = new double[nl];
        for (int i = 0; i < nl; i++)
        {
            var l = net.Links[i];
            q[i] = !open[i] ? 0 : pumps[i] is { } pm ? pm.DesignFlow * l.Speed : Math.PI * l.Diameter * l.Diameter / 4 * Ft;
        }
        double accuracy = options.Accuracy ?? net.Accuracy;
        var p = new double[nl]; var y = new double[nl];
        var hl = new double[nl];
        bool[] disconnected = new bool[nn];
        int iter = 0; double rel = double.MaxValue;
        var junctions = Enumerable.Range(0, nn).Where(i => !fixedHead[i]).ToArray();
        var jIndex = new int[nn]; Array.Fill(jIndex, -1);
        for (int k = 0; k < junctions.Length; k++) jIndex[junctions[k]] = k;

        for (int statusRound = 0; statusRound < 20; statusRound++)
        {
            disconnected = Disconnected(nn, nl, from, to, open, fixedHead);
            for (iter = iter == 0 ? 0 : iter; iter < options.MaxIterations; iter++)
            {
                for (int k = 0; k < nl; k++)
                {
                    if (!open[k] || disconnected[from[k]] || disconnected[to[k]]) { p[k] = 0; y[k] = 0; q[k] = 0; continue; }
                    (p[k], y[k]) = Coefficients(net, net.Links[k], pumps[k], q[k]);
                }
                // assemble A·H = F over junctions (symmetric, positive definite)
                int m = junctions.Length;
                var diag = new double[m]; var rhs = new double[m];
                var off = new Dictionary<long, double>();
                for (int jk = 0; jk < m; jk++) if (!disconnected[junctions[jk]]) rhs[jk] = -demand[junctions[jk]];
                for (int k = 0; k < nl; k++)
                {
                    if (p[k] == 0) continue;
                    int a = from[k], b = to[k], ja = jIndex[a], jb = jIndex[b];
                    double qy = q[k] - y[k];
                    if (ja >= 0) { diag[ja] += p[k]; rhs[ja] -= qy; }
                    if (jb >= 0) { diag[jb] += p[k]; rhs[jb] += qy; }
                    if (ja >= 0 && jb >= 0) { long key = Math.Min(ja, jb) * (long)m + Math.Max(ja, jb); off[key] = off.GetValueOrDefault(key) - p[k]; }
                    else if (ja >= 0) rhs[ja] += p[k] * head[b];
                    else if (jb >= 0) rhs[jb] += p[k] * head[a];
                }
                for (int jk = 0; jk < m; jk++) if (disconnected[junctions[jk]]) { diag[jk] = 1; rhs[jk] = 0; }
                var h = ConjugateGradient(m, diag, off, rhs, junctions.Select(j => head[j]).ToArray());
                for (int jk = 0; jk < m; jk++) head[junctions[jk]] = disconnected[junctions[jk]] ? double.NaN : h[jk];
                double sumDq = 0, sumQ = 0;
                for (int k = 0; k < nl; k++)
                {
                    if (p[k] == 0) continue;
                    double dq = y[k] - p[k] * (head[from[k]] - head[to[k]]);
                    q[k] -= dq;
                    sumDq += Math.Abs(dq); sumQ += Math.Abs(q[k]);
                }
                rel = sumQ > 0 ? sumDq / sumQ : 0;
                if (rel <= accuracy) { iter++; break; }
            }
            if (!CheckStatus(net, pumps, open, q, head, from, to)) break;
        }

        for (int k = 0; k < nl; k++)
            hl[k] = !open[k] || p[k] == 0 ? (open[k] ? 0 : head[from[k]] - head[to[k]]) : HeadLoss(net, net.Links[k], pumps[k], q[k]);
        // node outflow balance: junction demand, or the net outflow of reservoirs and tanks
        var outDemand = new double[nn];
        for (int i = 0; i < nn; i++) outDemand[i] = fixedHead[i] ? 0 : disconnected[i] ? 0 : demand[i];
        for (int k = 0; k < nl; k++)
        {
            if (fixedHead[from[k]]) outDemand[from[k]] -= q[k];
            if (fixedHead[to[k]]) outDemand[to[k]] += q[k];
        }
        return new HydraulicResult(net, head, outDemand, q, hl, open, disconnected, iter, rel);
    }

    /// <summary>Nodes not connected through open links to any fixed-head node.</summary>
    private static bool[] Disconnected(int nn, int nl, int[] from, int[] to, bool[] open, bool[] fixedHead)
    {
        var adj = new List<int>[nn];
        for (int i = 0; i < nn; i++) adj[i] = [];
        for (int k = 0; k < nl; k++) if (open[k]) { adj[from[k]].Add(to[k]); adj[to[k]].Add(from[k]); }
        var seen = new bool[nn];
        var stack = new Stack<int>();
        for (int i = 0; i < nn; i++) if (fixedHead[i]) { seen[i] = true; stack.Push(i); }
        while (stack.Count > 0) foreach (var j in adj[stack.Pop()]) if (!seen[j]) { seen[j] = true; stack.Push(j); }
        return seen.Select(s => !s).ToArray();
    }

    /// <summary>EPANET-style status checks: check valves close on reverse gradient, pumps close when they cannot deliver the head.</summary>
    private static bool CheckStatus(WaterNetwork net, PumpModel?[] pumps, bool[] open, double[] q, double[] head, int[] from, int[] to)
    {
        bool changed = false;
        for (int k = 0; k < net.Links.Count; k++)
        {
            var l = net.Links[k];
            double dh = head[from[k]] - head[to[k]];
            if (double.IsNaN(dh)) continue;
            if (l.Kind == WaterLinkKind.Pipe && l.CheckValve && l.Status == LinkStatus.Open)
            {
                bool shouldOpen = open[k] ? q[k] > -1e-9 : dh > 1e-6;
                if (shouldOpen != open[k]) { open[k] = shouldOpen; changed = true; if (!shouldOpen) q[k] = 0; else q[k] = 1e-6; }
            }
            else if (l.Kind == WaterLinkKind.Pump && pumps[k] is { } pm && l.Status == LinkStatus.Open && pm.ShutoffHead is { } h0)
            {
                double gainNeeded = -dh;
                double shut = h0 * l.Speed * l.Speed;
                if (open[k] && (gainNeeded > shut + 1e-6 || q[k] < -1e-9)) { open[k] = false; q[k] = 0; changed = true; }
            }
        }
        return changed;
    }

    /// <summary>Head loss over a link at flow q (m).</summary>
    private static double HeadLoss(WaterNetwork net, WaterLink l, PumpModel? pump, double q)
    {
        if (pump is not null) return -pump.Gain(q, l.Speed);
        double aq = Math.Abs(q);
        double r = Resistance(net, l, aq, out double n);
        double minor = MinorK * MinorCoefficient(l) / Math.Pow(l.Diameter, 4);
        return Math.Sign(q) * (r * Math.Pow(aq, n) + minor * aq * aq);
    }

    private static double MinorCoefficient(WaterLink l) => l.MinorLoss + (l.Kind == WaterLinkKind.Valve && l.ValveType == "TCV" ? l.ValveSetting : 0);

    /// <summary>Pipe resistance r and exponent n of h = r·qⁿ.</summary>
    private static double Resistance(WaterNetwork net, WaterLink l, double aq, out double n)
    {
        if (l.Kind == WaterLinkKind.Valve) { n = 2; return 0; }
        double d = l.Diameter, len = l.Length;
        switch (net.Headloss)
        {
            case HeadlossFormula.HazenWilliams:
                n = 1.852;
                return HwK * len / (Math.Pow(l.Roughness, 1.852) * Math.Pow(d, 4.871));
            case HeadlossFormula.ChezyManning:
                n = 2;
                // h = 10.29 n² L q² / d^5.33 (SI)
                return 10.294 * l.Roughness * l.Roughness * len / Math.Pow(d, 16.0 / 3);
            default:
                n = 2;
                double area = Math.PI * d * d / 4, v = aq / area, re = v * d / Viscosity;
                double f = FrictionFactor(re, l.Roughness / d);
                // EPANET 2.2 resistcoeff(): r = L / (2·g·d·A²) with g = 32.2 ft/s² (9.8146 m/s²)
                return f * len / (2 * 32.2 * Ft * d * area * area);
        }
    }

    /// <summary>Darcy friction factor: laminar 64/Re, Swamee-Jain above Re 4000, Dunlop's cubic interpolation in between (EPANET 2).</summary>
    private static double FrictionFactor(double re, double rel)
    {
        if (re < 1e-9) return 64 / 1e-9;
        if (re < 2000) return 64 / re;
        if (re >= 4000) { double a = Math.Log10(rel / 3.7 + 5.74 / Math.Pow(re, 0.9)); return 0.25 / (a * a); }
        double y3 = -0.86859 * Math.Log(rel / 3.7 + 5.74 / Math.Pow(4000, 0.9));
        double y2 = rel / 3.7 + 5.74 / Math.Pow(4000, 0.9);
        double fa = 1 / (y3 * y3), fb = (2 + 0.00514215 / (y2 * y3)) * fa;
        double rr = re / 2000;
        double x1 = 7 * fa - fb, x2 = 0.128 - 17 * fa + 2.5 * fb, x3 = -0.128 + 13 * fa - (fb + fb), x4 = 0.032 - 3 * fa + 0.5 * fb;
        return x1 + rr * (x2 + rr * (x3 + rr * x4));
    }

    /// <summary>GGA coefficients p = 1/(dh/dq) and y = p·h(q).</summary>
    private static (double P, double Y) Coefficients(WaterNetwork net, WaterLink l, PumpModel? pump, double q)
    {
        const double qTol = 1e-7 * Cfs;
        if (pump is not null)
        {
            double g = Math.Max(pump.Gradient(Math.Max(q, qTol), l.Speed), RqTol);
            double pp = 1 / g;
            return (pp, pp * -pump.Gain(q, l.Speed));
        }
        double aq = Math.Abs(q);
        double minor = MinorK * MinorCoefficient(l) / Math.Pow(l.Diameter, 4);
        double r = Resistance(net, l, Math.Max(aq, qTol), out double n);
        if (net.Headloss == HeadlossFormula.DarcyWeisbach && l.Kind == WaterLinkKind.Pipe)
        {
            // numerical gradient (the friction factor depends on q)
            double dq = Math.Max(aq * 1e-6, 1e-10);
            double h1 = HeadLoss(net, l, null, aq + dq), h0 = HeadLoss(net, l, null, Math.Max(aq - dq, 0));
            double gdw = Math.Max((h1 - h0) / (aq + dq - Math.Max(aq - dq, 0)), RqTol);
            return (1 / gdw, HeadLoss(net, l, null, q) / gdw);
        }
        double grad = n * r * Math.Pow(Math.Max(aq, qTol), n - 1) + 2 * minor * aq;
        if (grad < RqTol) grad = RqTol;
        double ph = 1 / grad;
        return (ph, ph * HeadLoss(net, l, null, q));
    }

    /// <summary>Jacobi-preconditioned conjugate gradient for the symmetric head system.</summary>
    private static double[] ConjugateGradient(int m, double[] diag, Dictionary<long, double> off, double[] b, double[] x0)
    {
        var rows = new List<(int Col, double V)>[m];
        for (int i = 0; i < m; i++) rows[i] = [];
        foreach (var (key, v) in off)
        {
            int i = (int)(key / m), j = (int)(key % m);
            rows[i].Add((j, v)); rows[j].Add((i, v));
        }
        var x = x0.Select(v => double.IsNaN(v) ? 0 : v).ToArray();
        double[] Mul(double[] v)
        {
            var r = new double[m];
            for (int i = 0; i < m; i++)
            {
                double s = diag[i] * v[i];
                foreach (var (c, w) in rows[i]) s += w * v[c];
                r[i] = s;
            }
            return r;
        }
        var ax = Mul(x);
        var res = new double[m]; var z = new double[m]; var pdir = new double[m];
        for (int i = 0; i < m; i++) { res[i] = b[i] - ax[i]; z[i] = res[i] / diag[i]; pdir[i] = z[i]; }
        double rz = Dot(res, z), bnorm = Math.Sqrt(Dot(b, b));
        if (bnorm == 0) bnorm = 1;
        for (int it = 0; it < 10 * m + 100; it++)
        {
            if (Math.Sqrt(Dot(res, res)) <= 1e-14 * bnorm) break;
            var ap = Mul(pdir);
            double alpha = rz / Dot(pdir, ap);
            for (int i = 0; i < m; i++) { x[i] += alpha * pdir[i]; res[i] -= alpha * ap[i]; z[i] = res[i] / diag[i]; }
            double rzNew = Dot(res, z);
            double beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < m; i++) pdir[i] = z[i] + beta * pdir[i];
        }
        return x;
    }

    private static double Dot(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>Pump characteristic: power function h = h0 − r·qⁿ, piecewise-linear table, or constant power.</summary>
    private sealed class PumpModel
    {
        private double _h0, _r, _n;
        private (double Q, double H)[]? _table;
        private double _powerKw;
        public double DesignFlow { get; private init; }
        public double? ShutoffHead => _table is not null ? _table[0].H : _powerKw > 0 ? null : _h0;

        public static PumpModel ConstantPower(double kw) => new() { _powerKw = kw, DesignFlow = Cfs };

        public static PumpModel FromCurve(List<(double X, double Y)> pts)
        {
            if (pts.Count == 1)
            {
                // EPANET: (0, 4/3·h1), (q1, h1), (2·q1, 0)
                var (q1, h1) = pts[0];
                return Power(1.33334 * h1, h1, 0, q1, 2 * q1, q1);
            }
            if (pts.Count == 3 && pts[0].X == 0)
                return Power(pts[0].Y, pts[1].Y, pts[2].Y, pts[1].X, pts[2].X, pts[1].X);
            return new PumpModel { _table = pts.Select(p => (p.X, p.Y)).ToArray(), DesignFlow = pts[pts.Count / 2].X };
        }

        private static PumpModel Power(double h0, double h1, double h2, double q1, double q2, double design)
        {
            double h4 = h0 - h1, h5 = h0 - h2;
            double n = Math.Log(h5 / h4) / Math.Log(q2 / q1);
            double r = h4 / Math.Pow(q1, n);
            return new PumpModel { _h0 = h0, _r = r, _n = n, DesignFlow = design };
        }

        /// <summary>Head gain at flow q with relative speed s (affinity laws).</summary>
        public double Gain(double q, double s)
        {
            if (_powerKw > 0)
            {
                double qq = Math.Max(q, 1e-9);
                return Ft * 8.814 * (_powerKw / 0.745699872) / (qq / Cfs);
            }
            if (_table is { } t)
            {
                double qs = q / s;
                int i = 1;
                while (i < t.Length - 1 && qs > t[i].Q) i++;
                double slope = (t[i].H - t[i - 1].H) / (t[i].Q - t[i - 1].Q);
                return s * s * (t[i - 1].H + slope * (qs - t[i - 1].Q));
            }
            double aq = Math.Max(q, 0);
            return s * s * _h0 - _r * Math.Pow(s, 2 - _n) * Math.Pow(aq, _n);
        }

        /// <summary>−d(gain)/dq ≥ 0.</summary>
        public double Gradient(double q, double s)
        {
            if (_powerKw > 0) return Gain(q, s) / Math.Max(q, 1e-9);
            if (_table is { } t)
            {
                double qs = q / s;
                int i = 1;
                while (i < t.Length - 1 && qs > t[i].Q) i++;
                return -(t[i].H - t[i - 1].H) / (t[i].Q - t[i - 1].Q) * s;
            }
            return _n * _r * Math.Pow(s, 2 - _n) * Math.Pow(q, _n - 1);
        }
    }
}
