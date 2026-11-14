using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.Core.Units;
using Electrical.PowerFlow;

namespace Electrical.Harmonic;

/// <summary>A harmonic current source at a bus.</summary>
/// <param name="Bus">Bus id.</param>
/// <param name="Spectrum">Current spectrum.</param>
/// <param name="FundamentalCurrentPu">
/// Fundamental current magnitude [pu on the system base]; null = derived from the in-service loads at the bus and the fundamental power flow
/// (I₁ = conj(S/V)), with harmonic phases shifted by h·θ₁.
/// </param>
public sealed record HarmonicInjection(int Bus, HarmonicSourceSpectrum Spectrum, double? FundamentalCurrentPu = null);

/// <summary>Options for harmonic network models.</summary>
public sealed record HarmonicOptions
{
    /// <summary>Highest harmonic order.</summary>
    public int MaxOrder { get; init; } = 49;
    /// <summary>Model loads as parallel R–L impedances (R = V²/P, X = h·V²/Q); false = loads omitted.</summary>
    public bool IncludeLoads { get; init; } = true;
    /// <summary>Use the zero-sequence network for triplen orders (h = 3k) and the negative-sequence generator reactance for h = 3k − 1.</summary>
    public bool UseSequenceNetworks { get; init; } = true;
    /// <summary>Resistance of generators scales with √h (skin effect) when true; otherwise constant.</summary>
    public bool SkinEffect { get; init; }

    public static HarmonicOptions Default { get; } = new();
}

/// <summary>Builds per-harmonic bus admittance matrices (frequency-scaled reactances and susceptances).</summary>
public static class HarmonicNetwork
{
    /// <summary>Sequence that a balanced harmonic of order h belongs to (h mod 3 = 1 positive, 2 negative, 0 zero).</summary>
    public static Sequence SequenceOf(int h) => (h % 3) switch { 1 => Sequence.Positive, 2 => Sequence.Negative, _ => Sequence.Zero };

    /// <summary>
    /// Y-bus at harmonic order <paramref name="h"/> (real-valued h allowed for scans) [pu]: branch series R + j·h·X, charging j·h·B,
    /// capacitive shunts j·h·B, inductive shunts j·B/h, generators 1/(R_a + j·h·X″d), loads P/V² − jQ/(h·V²).
    /// </summary>
    public static (SparseMatrix<Complex> Y, BranchAdmittance[] Branches) Build(PowerNetwork net, double h, Sequence sequence = Sequence.Positive,
        HarmonicOptions? options = null, IReadOnlyList<double>? busVoltagesPu = null)
    {
        var opt = options ?? HarmonicOptions.Default;
        int n = net.BusCount;
        var b = new SparseBuilder<Complex>(n, n);
        var adm = new BranchAdmittance[net.Branches.Count];
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            if (!br.InService) continue;
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            bool zero = sequence == Sequence.Zero;
            double r = zero ? br.ZeroSeqR : br.R, x = (zero ? br.ZeroSeqX : br.X) * h, bc = (zero ? br.ZeroSeqB : br.B) * h;
            if (r == 0 && x == 0) x = 1e-6;
            var ys = 1 / new Complex(r, x);
            if (zero && br.IsTransformer)
            {
                switch (br.Connection)
                {
                    case WindingConnection.YNyn: break;
                    case WindingConnection.YNd: b.Add(f, f, ys); continue;
                    case WindingConnection.Dyn: b.Add(t, t, ys); continue;
                    default: continue;
                }
            }
            var tap = Complex.FromPolarCoordinates(br.EffectiveTap, (sequence == Sequence.Negative ? -br.ShiftDeg : zero ? 0 : br.ShiftDeg) * Scalar.Deg2Rad);
            var ytt = ys + new Complex(0, bc / 2);
            var a = new BranchAdmittance(ytt / (tap * Complex.Conjugate(tap)), -ys / Complex.Conjugate(tap), -ys / tap, ytt);
            adm[k] = a;
            b.Add(f, f, a.Yff); b.Add(f, t, a.Yft); b.Add(t, f, a.Ytf); b.Add(t, t, a.Ytt);
        }
        for (int i = 0; i < n; i++)
        {
            var bus = net.Buses[i];
            double bs = bus.Bs >= 0 ? bus.Bs * h : bus.Bs / h;
            var y = sequence == Sequence.Zero ? Complex.Zero : new Complex(bus.Gs, bs) / net.BaseMva;
            b.Add(i, i, y + 1e-9);
        }
        foreach (var g in net.Generators)
        {
            if (!g.InService) continue;
            int i = net.IndexOf(g.Bus);
            double x = sequence switch
            {
                Sequence.Negative when opt.UseSequenceNetworks => g.X2 > 0 ? g.X2 : g.XdSubtransient,
                Sequence.Zero => g.X0,
                _ => g.XdSubtransient,
            };
            if (x <= 0) continue;
            double ra = opt.SkinEffect ? g.Ra * Math.Sqrt(h) : g.Ra;
            b.Add(i, i, 1 / (new Complex(ra, x * h) * net.BaseMva / g.MBase));
        }
        if (opt.IncludeLoads && sequence != Sequence.Zero)
        {
            foreach (var l in net.Loads)
            {
                if (!l.InService) continue;
                int i = net.IndexOf(l.Bus);
                double v = busVoltagesPu is null ? 1.0 : busVoltagesPu[i];
                double v2 = v * v;
                var y = new Complex(l.P / net.BaseMva / v2, l.Q > 0 ? -l.Q / net.BaseMva / (v2 * h) : -l.Q / net.BaseMva * h / v2);
                b.Add(i, i, y);
            }
        }
        return (b.ToCsr(), adm);
    }
}

/// <summary>Harmonic load-flow result (current-injection method).</summary>
public sealed class HarmonicLoadFlowResult
{
    internal HarmonicLoadFlowResult(PowerNetwork net, PowerFlowResult fundamental, Dictionary<int, Complex[]> v, Dictionary<int, BranchAdmittance[]> adm)
    {
        Network = net; Fundamental = fundamental; _v = v; _adm = adm;
    }

    private readonly Dictionary<int, Complex[]> _v;
    private readonly Dictionary<int, BranchAdmittance[]> _adm;

    public PowerNetwork Network { get; }
    /// <summary>Fundamental-frequency power flow used for source phase angles and load models.</summary>
    public PowerFlowResult Fundamental { get; }
    /// <summary>Orders with non-zero injections.</summary>
    public IReadOnlyCollection<int> Orders => _v.Keys;

    /// <summary>Harmonic voltage phasor of order h at a bus [pu].</summary>
    public Complex Voltage(int busId, int order) =>
        _v.TryGetValue(order, out var v) ? v[Network.IndexOf(busId)] : order == 1 ? Fundamental.Voltages[Network.IndexOf(busId)] : Complex.Zero;

    /// <summary>Voltage spectrum at a bus [pu].</summary>
    public HarmonicSpectrum VoltageSpectrum(int busId)
    {
        int hMax = _v.Count == 0 ? 1 : Math.Max(1, _v.Keys.Max());
        var rms = new double[hMax + 1];
        var ph = new double[hMax + 1];
        for (int h = 1; h <= hMax; h++)
        {
            var c = Voltage(busId, h);
            rms[h] = c.Magnitude; ph[h] = c.AngleDeg();
        }
        return new HarmonicSpectrum(Network.FrequencyHz, rms, ph);
    }

    /// <summary>Voltage THD at a bus [%].</summary>
    public double ThdPercent(int busId) => VoltageSpectrum(busId).ThdPercent;

    /// <summary>Bus with the highest voltage THD.</summary>
    public (int BusId, double ThdPercent) WorstBus => Network.Buses.Select(b => (b.Id, ThdPercent(b.Id))).MaxBy(t => t.Item2);

    /// <summary>Harmonic current of order h in a branch at its from end [pu].</summary>
    public Complex BranchCurrent(int branchIndex, int order)
    {
        if (!_v.TryGetValue(order, out var v)) return Complex.Zero;
        var br = Network.Branches[branchIndex];
        var a = _adm[order][branchIndex];
        return a.Yff * v[Network.IndexOf(br.From)] + a.Yft * v[Network.IndexOf(br.To)];
    }

    /// <summary>IEEE 519 voltage check at every bus.</summary>
    public IReadOnlyList<(int BusId, ComplianceResult Result)> CheckIeee519() =>
        Network.Buses.Select(b => (b.Id, Ieee519.CheckVoltage(VoltageSpectrum(b.Id), b.BaseKv))).ToList();
}

/// <summary>Harmonic load flow by the current-injection method: for every order h, solve Y(h)·V(h) = I(h).</summary>
public static class HarmonicLoadFlow
{
    /// <summary>Runs the harmonic load flow.</summary>
    /// <param name="network">Network (loads at source buses define the fundamental current unless given explicitly).</param>
    /// <param name="sources">Harmonic current sources.</param>
    /// <param name="options">Model options.</param>
    /// <param name="fundamental">Solved fundamental power flow (run with Newton–Raphson when null).</param>
    public static HarmonicLoadFlowResult Run(PowerNetwork network, IEnumerable<HarmonicInjection> sources, HarmonicOptions? options = null,
        PowerFlowResult? fundamental = null)
    {
        var opt = options ?? HarmonicOptions.Default;
        fundamental ??= PowerFlowSolver.Solve(network);
        var vm = fundamental.Voltages.Select(v => v.Magnitude).ToArray();
        int n = network.BusCount;
        // Harmonic injections per order (drawn currents enter the network with negative sign)
        var inj = new Dictionary<int, Complex[]>();
        foreach (var s in sources)
        {
            int i = network.IndexOf(s.Bus);
            var v1 = fundamental.Voltages[i];
            Complex i1;
            if (s.FundamentalCurrentPu is double mag) i1 = Complex.FromPolarCoordinates(mag, v1.Phase);
            else
            {
                var sl = network.Loads.Where(l => l.InService && l.Bus == s.Bus).Aggregate(Complex.Zero, (a, l) => a + new Complex(l.P, l.Q)) / network.BaseMva;
                i1 = Complex.Conjugate(sl / v1);
            }
            foreach (var c in s.Spectrum.Components)
            {
                if (c.Order < 2 || c.Order > opt.MaxOrder) continue;
                if (!inj.TryGetValue(c.Order, out var arr)) inj[c.Order] = arr = new Complex[n];
                double ang = c.PhaseDeg * Scalar.Deg2Rad + c.Order * i1.Phase;
                arr[i] -= Complex.FromPolarCoordinates(i1.Magnitude * c.MagnitudePercent / 100, ang);
            }
        }
        var volts = new Dictionary<int, Complex[]>();
        var adm = new Dictionary<int, BranchAdmittance[]>();
        foreach (var (h, current) in inj.OrderBy(k => k.Key))
        {
            var seq = opt.UseSequenceNetworks ? HarmonicNetwork.SequenceOf(h) : Sequence.Positive;
            var (y, a) = HarmonicNetwork.Build(network, h, seq, opt, vm);
            volts[h] = SparseLU<Complex>.Factorize(y).Solve(current);
            adm[h] = a;
        }
        return new HarmonicLoadFlowResult(network, fundamental, volts, adm);
    }
}

/// <summary>One point of a frequency scan.</summary>
public readonly record struct FrequencyScanPoint(double Order, double FrequencyHz, Complex ImpedancePu, double ImpedanceOhm);

/// <summary>A detected resonance (local extremum of |Z(h)|).</summary>
public sealed record Resonance(double Order, double FrequencyHz, double ImpedanceOhm, bool Parallel);

/// <summary>Driving-point impedance versus harmonic order and resonance detection.</summary>
public static class FrequencyScan
{
    /// <summary>Driving-point impedance Z_kk(h) at <paramref name="busId"/> for h = hMin … hMax in steps of <paramref name="step"/>.</summary>
    public static IReadOnlyList<FrequencyScanPoint> DrivingPoint(PowerNetwork network, int busId, double hMin = 1, double hMax = 50, double step = 0.05,
        HarmonicOptions? options = null)
    {
        int k = network.IndexOf(busId);
        double zb = new PerUnitBase(network.BaseMva, network.GetBus(busId).BaseKv).BaseOhm;
        var list = new List<FrequencyScanPoint>();
        int count = (int)Math.Floor((hMax - hMin) / step + 1e-9) + 1;
        for (int s = 0; s < count; s++)
        {
            double h = hMin + s * step;
            var (y, _) = HarmonicNetwork.Build(network, h, Sequence.Positive, options);
            var e = new Complex[network.BusCount];
            e[k] = 1;
            var z = SparseLU<Complex>.Factorize(y).Solve(e)[k];
            list.Add(new FrequencyScanPoint(h, h * network.FrequencyHz, z, z.Magnitude * zb));
        }
        return list;
    }

    /// <summary>Local maxima (parallel resonance) and minima (series resonance) of |Z| in a scan.</summary>
    public static IReadOnlyList<Resonance> FindResonances(IReadOnlyList<FrequencyScanPoint> scan)
    {
        var r = new List<Resonance>();
        for (int i = 1; i < scan.Count - 1; i++)
        {
            double a = scan[i - 1].ImpedanceOhm, b = scan[i].ImpedanceOhm, c = scan[i + 1].ImpedanceOhm;
            if (b > a && b >= c) r.Add(new Resonance(scan[i].Order, scan[i].FrequencyHz, b, true));
            else if (b < a && b <= c) r.Add(new Resonance(scan[i].Order, scan[i].FrequencyHz, b, false));
        }
        return r;
    }

    /// <summary>Parallel resonance order of a capacitor bank with the source: h_r = √(S_sc / Q_c).</summary>
    /// <param name="shortCircuitMva">Short-circuit power at the bus [MVA].</param>
    /// <param name="capacitorMvar">Capacitor bank rating [Mvar].</param>
    public static double ParallelResonanceOrder(double shortCircuitMva, double capacitorMvar) => Math.Sqrt(shortCircuitMva / capacitorMvar);
}
