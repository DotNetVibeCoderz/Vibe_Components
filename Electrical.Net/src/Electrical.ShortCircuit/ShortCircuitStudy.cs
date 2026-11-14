using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.Core.Units;

namespace Electrical.ShortCircuit;

/// <summary>
/// IEC 60909 short-circuit study on a <see cref="PowerNetwork"/> using the equivalent voltage source c·U_n/√3 at the fault location.
/// The positive-, negative- and zero-sequence bus admittance matrices (branches + source impedances, loads and shunts neglected)
/// are factorised once with <see cref="SparseLU{T}"/>; each fault then costs one sparse solve per sequence (one Z-bus column).
/// </summary>
/// <remarks>
/// Limitations: transformer phase shifts are ignored by default (they do not change magnitudes at the fault location);
/// the breaking current assumes far-from-generator conditions (I_b = I″k; use <see cref="Iec60909.DecayFactorMu"/> for near-generator faults);
/// pre-fault load currents are not superimposed (IEC 60909 method).
/// </remarks>
public sealed class ShortCircuitStudy
{
    private const double Regularization = 1e-7; // tiny shunt [pu] so buses without a zero-sequence path stay solvable (Z ≈ 10⁷ pu)

    private readonly PowerNetwork _net;
    private readonly ShortCircuitOptions _opt;
    private readonly SparseLU<Complex>[] _lu = new SparseLU<Complex>[3];
    private readonly SparseLU<Complex>? _luC;
    private readonly BranchAdmittance[][] _branchY = new BranchAdmittance[3][];
    private readonly List<Source> _sources = [];
    private readonly double _minRx;
    private readonly double _fcRatio;

    private sealed record Source(string Name, int BusIndex, bool IsGrid, Complex Y1, Complex Y2, Complex Y0, Complex Y1c);

    /// <summary>Prepares the sequence networks of <paramref name="network"/>.</summary>
    public ShortCircuitStudy(PowerNetwork network, ShortCircuitOptions? options = null)
    {
        _net = network ?? throw new ArgumentNullException(nameof(network));
        _opt = options ?? ShortCircuitOptions.Default;
        double f = network.FrequencyHz > 0 ? network.FrequencyHz : 50;
        _fcRatio = (f >= 55 ? 24.0 : 20.0) / f;

        BuildSources();
        _minRx = double.PositiveInfinity;
        for (int s = 0; s < 3; s++)
        {
            var (y, adm) = BuildY((Sequence)s, 1.0);
            _branchY[s] = adm;
            _lu[s] = SparseLU<Complex>.Factorize(y);
        }
        if (_opt.PeakMethod == PeakCurrentMethod.EquivalentFrequency)
            _luC = SparseLU<Complex>.Factorize(BuildY(Sequence.Positive, _fcRatio).Y);
        if (_opt.PeakMethod == PeakCurrentMethod.UniformRatio)
        {
            foreach (var br in network.Branches.Where(b => b.InService && b.X > 0)) _minRx = Math.Min(_minRx, br.R / br.X);
            foreach (var src in _sources)
            {
                var z = 1 / src.Y1;
                if (z.Imaginary > 0) _minRx = Math.Min(_minRx, z.Real / z.Imaginary);
            }
            if (double.IsInfinity(_minRx)) _minRx = 0;
        }
    }

    public PowerNetwork Network => _net;
    public ShortCircuitOptions Options => _opt;

    private void BuildSources()
    {
        var gridBuses = new HashSet<int>(_opt.ExternalGrids.Select(g => g.Bus));
        double baseMva = _net.BaseMva;
        foreach (var g in _net.Generators)
        {
            if (!g.InService || gridBuses.Contains(g.Bus) || g.XdSubtransient <= 0) continue;
            int i = _net.IndexOf(g.Bus);
            double scale = baseMva / g.MBase;
            double kg = 1;
            if (_opt.ApplyGeneratorCorrection)
            {
                double cmax = Iec60909.VoltageFactor(_net.Buses[i].BaseKv, true, _opt.LowVoltageTolerance);
                kg = Iec60909.GeneratorCorrectionFactor(cmax, g.XdSubtransient, _opt.GeneratorRatedPowerFactor);
            }
            Complex Z(double x, double fs) => new Complex(g.Ra, x * fs) * kg * scale;
            double x2 = g.X2 > 0 ? g.X2 : g.XdSubtransient;
            var y0 = _opt.GeneratorNeutralGrounded && g.X0 > 0 ? 1 / Z(g.X0, 1) : Complex.Zero;
            _sources.Add(new Source(g.Name ?? $"Gen@{g.Bus}", i, false, 1 / Z(g.XdSubtransient, 1), 1 / Z(x2, 1), y0, 1 / Z(g.XdSubtransient, _fcRatio)));
        }
        foreach (var eg in _opt.ExternalGrids)
        {
            int i = _net.IndexOf(eg.Bus);
            double zq = eg.C * baseMva / eg.SkMva; // pu on the system base at the bus nominal voltage
            double x = zq / Math.Sqrt(1 + eg.RxRatio * eg.RxRatio);
            var z1 = new Complex(eg.RxRatio * x, x);
            var z1c = new Complex(eg.RxRatio * x, x * _fcRatio);
            var y0 = double.IsNaN(eg.Z0OverZ1) || eg.Z0OverZ1 <= 0 ? Complex.Zero : 1 / (z1 * eg.Z0OverZ1);
            _sources.Add(new Source(eg.Name ?? $"Grid@{eg.Bus}", i, true, 1 / z1, 1 / z1, y0, 1 / z1c));
        }
        if (_sources.Count == 0) throw new InvalidOperationException("No short-circuit sources: add generators with subtransient data or external grids.");
    }

    private (SparseMatrix<Complex> Y, BranchAdmittance[] Adm) BuildY(Sequence seq, double reactanceScale)
    {
        int n = _net.BusCount;
        var b = new SparseBuilder<Complex>(n, n);
        var adm = new BranchAdmittance[_net.Branches.Count];
        for (int k = 0; k < _net.Branches.Count; k++)
        {
            var br = _net.Branches[k];
            if (!br.InService) continue;
            int f = _net.IndexOf(br.From), t = _net.IndexOf(br.To);
            double r = seq == Sequence.Zero ? br.ZeroSeqR : br.R;
            double x = (seq == Sequence.Zero ? br.ZeroSeqX : br.X) * reactanceScale;
            double bsh = _opt.IncludeShunts ? (seq == Sequence.Zero ? br.ZeroSeqB : br.B) * reactanceScale : 0;
            if (r == 0 && x == 0) x = 1e-6;
            var ys = 1 / new Complex(r, x);
            if (seq == Sequence.Zero && br.IsTransformer)
            {
                switch (br.Connection)
                {
                    case WindingConnection.YNyn: break;
                    case WindingConnection.YNd: b.Add(f, f, ys); continue;
                    case WindingConnection.Dyn: b.Add(t, t, ys); continue;
                    default: continue;
                }
            }
            Complex tap = Complex.One;
            if (_opt.UseTransformerTaps)
            {
                double shift = seq == Sequence.Negative ? -br.ShiftDeg : br.ShiftDeg;
                tap = Complex.FromPolarCoordinates(br.EffectiveTap, seq == Sequence.Zero ? 0 : shift * Scalar.Deg2Rad);
            }
            var bc = new Complex(0, bsh / 2);
            var ytt = ys + bc;
            var a = new BranchAdmittance(ytt / (tap * Complex.Conjugate(tap)), -ys / Complex.Conjugate(tap), -ys / tap, ytt);
            adm[k] = a;
            b.Add(f, f, a.Yff); b.Add(f, t, a.Yft); b.Add(t, f, a.Ytf); b.Add(t, t, a.Ytt);
        }
        for (int i = 0; i < n; i++)
        {
            var sh = Complex.Zero;
            if (_opt.IncludeShunts && seq != Sequence.Zero) sh = new Complex(_net.Buses[i].Gs, _net.Buses[i].Bs * reactanceScale) / _net.BaseMva;
            b.Add(i, i, sh + Regularization);
        }
        foreach (var s in _sources)
        {
            var y = reactanceScale != 1.0 ? s.Y1c : seq switch { Sequence.Positive => s.Y1, Sequence.Negative => s.Y2, _ => s.Y0 };
            if (y != Complex.Zero) b.Add(s.BusIndex, s.BusIndex, y);
        }
        return (b.ToCsr(), adm);
    }

    private Complex[] Column(SparseLU<Complex> lu, int k)
    {
        var e = new Complex[_net.BusCount];
        e[k] = Complex.One;
        return lu.Solve(e);
    }

    /// <summary>Voltage factor c applied at bus <paramref name="busId"/>.</summary>
    public double VoltageFactorAt(int busId) =>
        _opt.VoltageFactorOverride ?? Iec60909.VoltageFactor(_net.GetBus(busId).BaseKv, _opt.Maximum, _opt.LowVoltageTolerance);

    /// <summary>Thevenin (driving-point) sequence impedances Z1, Z2, Z0 at a bus [pu on the system base].</summary>
    public (Complex Z1, Complex Z2, Complex Z0) TheveninImpedance(int busId)
    {
        int k = _net.IndexOf(busId);
        return (Column(_lu[0], k)[k], Column(_lu[1], k)[k], Column(_lu[2], k)[k]);
    }

    /// <summary>Thevenin impedances at a bus in Ω (referred to the bus nominal voltage).</summary>
    public (Complex Z1, Complex Z2, Complex Z0) TheveninImpedanceOhm(int busId)
    {
        var (z1, z2, z0) = TheveninImpedance(busId);
        double zb = new PerUnitBase(_net.BaseMva, _net.GetBus(busId).BaseKv).BaseOhm;
        return (z1 * zb, z2 * zb, z0 * zb);
    }

    /// <summary>Computes a fault at <paramref name="busId"/>.</summary>
    /// <param name="busId">Faulted bus id.</param>
    /// <param name="type">Fault type.</param>
    /// <param name="faultImpedanceOhm">Fault impedance [Ω]; defaults to <see cref="ShortCircuitOptions.FaultImpedanceOhm"/>.</param>
    public FaultResult Calculate(int busId, FaultType type, Complex? faultImpedanceOhm = null)
    {
        int k = _net.IndexOf(busId);
        var bus = _net.Buses[k];
        var pb = new PerUnitBase(_net.BaseMva, bus.BaseKv);
        double c = VoltageFactorAt(busId);
        var zf = pb.OhmToPu(faultImpedanceOhm ?? _opt.FaultImpedanceOhm);
        var z1c = Column(_lu[0], k); var z2c = Column(_lu[1], k); var z0c = Column(_lu[2], k);
        Complex z1 = z1c[k], z2 = z2c[k], z0 = z0c[k];
        Complex e = c;
        Complex i1, i2, i0;
        switch (type)
        {
            case FaultType.ThreePhase:
                i1 = e / (z1 + zf); i2 = i0 = 0; break;
            case FaultType.SingleLineToGround:
                i1 = i2 = i0 = e / (z1 + z2 + z0 + 3 * zf); break;
            case FaultType.LineToLine:
                i1 = e / (z1 + z2 + zf); i2 = -i1; i0 = 0; break;
            case FaultType.DoubleLineToGround:
                {
                    var z0f = z0 + 3 * zf;
                    i1 = e / (z1 + z2 * z0f / (z2 + z0f));
                    i2 = -i1 * z0f / (z2 + z0f);
                    i0 = -i1 * z2 / (z2 + z0f);
                    break;
                }
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
        var seqI = new SequenceSet(i0, i1, i2);
        var phI = SymmetricalComponents.ToPhases(seqI);
        double ibase = pb.BaseKa;
        var phKa = new ThreePhase(phI.A * ibase, phI.B * ibase, phI.C * ibase);
        double ik = type switch
        {
            FaultType.ThreePhase or FaultType.SingleLineToGround => phKa.A.Magnitude,
            _ => Math.Max(phKa.B.Magnitude, phKa.C.Magnitude),
        };
        double earth = (3 * i0).Magnitude * ibase;

        // Bus voltages: V = E - Z(:,k)·I (positive), -Z·I (negative, zero)
        int n = _net.BusCount;
        var v1 = new Complex[n]; var v2 = new Complex[n]; var v0 = new Complex[n];
        var volts = new BusFaultVoltage[n];
        for (int i = 0; i < n; i++)
        {
            v1[i] = e - z1c[i] * i1; v2[i] = -z2c[i] * i2; v0[i] = -z0c[i] * i0;
            var ph = SymmetricalComponents.ToPhases(new SequenceSet(v0[i], v1[i], v2[i]));
            volts[i] = new BusFaultVoltage(_net.Buses[i].Id, _net.Buses[i].BaseKv, ph.A.Magnitude, ph.B.Magnitude, ph.C.Magnitude);
        }
        var branches = new List<BranchFaultCurrent>();
        for (int b = 0; b < _net.Branches.Count; b++)
        {
            var br = _net.Branches[b];
            if (!br.InService) continue;
            int f = _net.IndexOf(br.From), t = _net.IndexOf(br.To);
            Complex Flow(BranchAdmittance a, Complex vf, Complex vt) => a.Yff * vf + a.Yft * vt;
            var s = new SequenceSet(Flow(_branchY[2][b], v0[f], v0[t]), Flow(_branchY[0][b], v1[f], v1[t]), Flow(_branchY[1][b], v2[f], v2[t]));
            var ph = SymmetricalComponents.ToPhases(s);
            double kb = new PerUnitBase(_net.BaseMva, _net.Buses[f].BaseKv).BaseKa;
            branches.Add(new BranchFaultCurrent(b, br.From, br.To, br.Name, ph.A.Magnitude * kb, ph.B.Magnitude * kb, ph.C.Magnitude * kb,
                (3 * s.Zero).Magnitude * kb));
        }
        var sources = new List<SourceContribution>();
        foreach (var src in _sources)
        {
            int i = src.BusIndex;
            var s = new SequenceSet(-src.Y0 * v0[i], src.Y1 * (e - v1[i]), -src.Y2 * v2[i]);
            var ph = SymmetricalComponents.ToPhases(s);
            double kb = new PerUnitBase(_net.BaseMva, _net.Buses[i].BaseKv).BaseKa;
            sources.Add(new SourceContribution(src.Name, _net.Buses[i].Id, src.IsGrid, ph.A.Magnitude * kb, ph.B.Magnitude * kb, ph.C.Magnitude * kb));
        }

        double rx = RxForPeak(k, z1, out double kappa, bus.BaseKv);
        double f0 = _net.FrequencyHz > 0 ? _net.FrequencyHz : 50;
        double ip = kappa * Math.Sqrt(2) * ik;
        double idc = Iec60909.DcComponent(ik, rx, f0, _opt.MinimumTimeDelayS);
        double ith = Iec60909.ThermalEquivalentCurrent(ik, kappa, f0, _opt.FaultDurationS);
        return new FaultResult
        {
            BusId = busId, Type = type, BaseKv = bus.BaseKv, C = c, Z1 = z1, Z2 = z2, Z0 = z0, SequenceCurrentsPu = seqI, PhaseCurrentsKa = phKa,
            IkKa = ik, EarthCurrentKa = earth, RxRatio = rx, Kappa = kappa, IpKa = ip, IbKa = ik, IdcKa = idc,
            IbAsymKa = Math.Sqrt(ik * ik + idc * idc), IthKa = ith, Voltages = volts, Branches = branches, Sources = sources,
        };
    }

    private double RxForPeak(int k, Complex z1, out double kappa, double kv)
    {
        double rx;
        switch (_opt.PeakMethod)
        {
            case PeakCurrentMethod.UniformRatio:
                rx = _minRx; kappa = Iec60909.Kappa(rx); break;
            case PeakCurrentMethod.ImpedanceAtFault:
                rx = z1.Imaginary > 0 ? z1.Real / z1.Imaginary : 0;
                kappa = Math.Min(1.15 * Iec60909.Kappa(rx), kv <= 1 ? 1.8 : 2.0);
                break;
            default:
                var zc = Column(_luC!, k)[k];
                rx = zc.Imaginary > 0 ? zc.Real / zc.Imaginary * _fcRatio : 0;
                kappa = Iec60909.Kappa(rx);
                break;
        }
        return rx;
    }

    /// <summary>Three-phase, line-to-line, single-line-to-ground and double-line-to-ground currents at every bus.</summary>
    public IReadOnlyList<BusShortCircuitSummary> ScanAllBuses()
    {
        var rows = new BusShortCircuitSummary[_net.BusCount];
        Parallel.For(0, _net.BusCount, i =>
        {
            int id = _net.Buses[i].Id;
            var f3 = Calculate(id, FaultType.ThreePhase);
            var f1 = Calculate(id, FaultType.SingleLineToGround);
            var f2 = Calculate(id, FaultType.LineToLine);
            var f2e = Calculate(id, FaultType.DoubleLineToGround);
            rows[i] = new BusShortCircuitSummary(id, _net.Buses[i].Name, f3.BaseKv, f3.IkKa, f2.IkKa, f1.IkKa, f2e.IkKa, f3.IpKa, f3.SkMva,
                f3.RxRatio, f3.Z1, f3.Z0);
        });
        return rows;
    }
}

/// <summary>Convenience entry points for IEC 60909 short-circuit calculations.</summary>
public static class ShortCircuitAnalysis
{
    /// <summary>Computes one fault (builds and factorises the sequence networks; use <see cref="ShortCircuitStudy"/> for repeated faults).</summary>
    public static FaultResult Calculate(PowerNetwork net, int busId, FaultType type, ShortCircuitOptions? options = null) =>
        new ShortCircuitStudy(net, options).Calculate(busId, type);

    /// <summary>All-bus scan of I″k3, I″k2, I″k1, I″k2E, i_p and S″k.</summary>
    public static IReadOnlyList<BusShortCircuitSummary> ScanAllBuses(PowerNetwork net, ShortCircuitOptions? options = null) =>
        new ShortCircuitStudy(net, options).ScanAllBuses();
}
