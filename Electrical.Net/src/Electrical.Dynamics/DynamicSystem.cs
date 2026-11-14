using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;
using Electrical.Stability;

namespace Electrical.Dynamics;

/// <summary>Static load representation in dynamic simulation.</summary>
public enum LoadRepresentation
{
    /// <summary>Constant admittance from the initial power flow (default, as in <c>Stability.ClassicalModel</c>).</summary>
    ConstantImpedance,
    /// <summary>Each load's own <c>Load.Model</c> ZIP fractions.</summary>
    NetworkZip,
    /// <summary>The ZIP fractions of <see cref="DynamicSystemOptions.Zip"/> for every load.</summary>
    Zip,
}

/// <summary>Options for building a <see cref="DynamicSystem"/>.</summary>
public sealed record DynamicSystemOptions
{
    public LoadRepresentation Loads { get; init; } = LoadRepresentation.ConstantImpedance;
    /// <summary>ZIP fractions used with <see cref="LoadRepresentation.Zip"/>.</summary>
    public ZipModel Zip { get; init; } = ZipModel.ConstantImpedance;
    /// <summary>Below this voltage [pu] constant-power load parts turn into constant impedance (PSS/E PQBRAK).</summary>
    public double ConstantPowerBreakpoint { get; init; } = 0.7;
    /// <summary>Power-flow options for the initial operating point (tolerance tightened to 1e-12 pu when not given).</summary>
    public PowerFlowOptions? PowerFlow { get; init; }

    public static DynamicSystemOptions Default { get; } = new();
}

/// <summary>
/// Dynamic model of a network ready for time-domain simulation and linearisation: the DAE ẋ = f(x, V), 0 = I(x, V) − Y·V with
/// Y = Y-bus + load admittances + unit Norton admittances (+ fault admittances), all in pu on the system base, network frame rotating at
/// the nominal frequency. Initialized from a power flow so that f(x₀, V₀) = 0.
/// </summary>
public sealed partial class DynamicSystem
{
    private readonly PowerNetwork _net;          // private working copy (branch status changes)
    private readonly bool[] _branchBase;
    private readonly Complex[] _loadY0, _loadI0, _loadP0;  // per bus [pu] at the initial voltage
    private Complex[] _loadY, _loadI, _loadP;
    private readonly double[] _v0;
    private readonly Dictionary<int, Complex> _faults = [];
    private SparseLU<Complex>? _lu;
    private int[]? _ordering;
    private SparseMatrix<Complex>? _y;
    private readonly bool _nonlinear;
    private readonly List<int>[] _unitsAtBus;

    private DynamicSystem(PowerNetwork net, PowerFlowResult pf, List<DynamicUnit> units, DynamicSystemOptions opt, IReadOnlyList<string> warnings)
    {
        _net = net; PowerFlow = pf; Units = units; Options = opt; Warnings = warnings;
        int n = net.BusCount;
        BusCount = n;
        _branchBase = net.Branches.Select(b => b.InService).ToArray();
        _v0 = pf.Voltages.Select(v => v.Magnitude).ToArray();
        _loadY0 = new Complex[n]; _loadI0 = new Complex[n]; _loadP0 = new Complex[n];
        _unitsAtBus = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        for (int k = 0; k < units.Count; k++) _unitsAtBus[units[k].BusIndex].Add(k);
        // static loads
        var dynShare = new double[n];
        foreach (var u in units.OfType<ExponentialRecoveryLoad>()) dynShare[u.BusIndex] += u.Fraction;
        for (int i = 0; i < n; i++)
        {
            var b = pf.Buses[i];
            var s = new Complex(b.PLoad, b.QLoad) / net.BaseMva * Math.Max(0, 1 - dynShare[i]);
            var zip = LoadFractions(net, i, opt);
            double vm = _v0[i];
            _loadY0[i] = Complex.Conjugate(s * zip.Z) / (vm * vm);
            _loadI0[i] = s * zip.I;
            _loadP0[i] = s * zip.P;
        }
        _loadY = (Complex[])_loadY0.Clone(); _loadI = (Complex[])_loadI0.Clone(); _loadP = (Complex[])_loadP0.Clone();
        _nonlinear = units.Any(u => u.VoltageDependent) || _loadI0.Any(c => c != Complex.Zero) || _loadP0.Any(c => c != Complex.Zero);
        StateNames = units.SelectMany(u => u.StateNames).ToArray();
        StateCount = StateNames.Count;
        InitialState = new double[StateCount];
        InitialVoltages = (Complex[])pf.Voltages.Clone();
        for (int k = 0; k < units.Count; k++)
        {
            var u = units[k];
            Complex s;
            if (u is ExponentialRecoveryLoad dl)
            {
                var b = pf.Buses[u.BusIndex];
                s = new Complex(b.PLoad, b.QLoad) / net.BaseMva * dl.Fraction;
            }
            else
            {
                var g = pf.Generators.First(r => r.Index == u.GeneratorIndex);
                s = new Complex(g.P, g.Q) / net.BaseMva;
            }
            u.Initialize(pf.Voltages[u.BusIndex], s, InitialState.AsSpan(u.StateOffset, u.StateCount));
        }
        InitialReferences = units.Select(u => SnapshotReferences(u)).ToArray();
        Rebuild();
    }

    /// <summary>Copy of the network (branch status reflects the current configuration).</summary>
    public PowerNetwork Network => _net;
    /// <summary>Initial power flow.</summary>
    public PowerFlowResult PowerFlow { get; }
    public IReadOnlyList<DynamicUnit> Units { get; }
    public DynamicSystemOptions Options { get; }
    /// <summary>Build warnings (default models, unmatched data).</summary>
    public IReadOnlyList<string> Warnings { get; }
    public int BusCount { get; }
    public int StateCount { get; }
    /// <summary>Qualified state names "unit.MODEL.state".</summary>
    public IReadOnlyList<string> StateNames { get; }
    /// <summary>Equilibrium state vector.</summary>
    public double[] InitialState { get; }
    /// <summary>Power-flow bus voltages [pu].</summary>
    public Complex[] InitialVoltages { get; }
    public double SystemBase => _net.BaseMva;
    public double FrequencyHz => _net.FrequencyHz > 0 ? _net.FrequencyHz : 50;
    public double OmegaS => 2 * Math.PI * FrequencyHz;
    internal Dictionary<string, double>[] InitialReferences { get; }

    /// <summary>Unit of generator <paramref name="generatorIndex"/> (null when out of service).</summary>
    public DynamicUnit? UnitOf(int generatorIndex) => Units.FirstOrDefault(u => u.GeneratorIndex == generatorIndex);

    /// <summary>Index of a state by qualified name (or unique suffix), −1 when not found.</summary>
    public int StateIndex(string name)
    {
        for (int i = 0; i < StateCount; i++) if (string.Equals(StateNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        int hit = -1;
        for (int i = 0; i < StateCount; i++)
            if (StateNames[i].EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) { if (hit >= 0) return -1; hit = i; }
        return hit;
    }

    private static readonly string[] RefNames = ["Vref", "Pref", "Qref", "Efd", "Pm", "Ipcmd", "Iqcmd"];

    private static Dictionary<string, double> SnapshotReferences(DynamicUnit u)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in RefNames) if (u.TryGetReference(n, out double v)) d[n] = v;
        return d;
    }

    private static ZipModel LoadFractions(PowerNetwork net, int busIndex, DynamicSystemOptions opt)
    {
        if (opt.Loads == LoadRepresentation.ConstantImpedance) return ZipModel.ConstantImpedance;
        if (opt.Loads == LoadRepresentation.Zip) return opt.Zip;
        int id = net.Buses[busIndex].Id;
        double z = 0, i = 0, p = 0, w = 0;
        foreach (var l in net.Loads.Where(l => l.InService && l.Bus == id))
        {
            double s = Math.Abs(l.P) + Math.Abs(l.Q);
            double t = l.Model.Z + l.Model.I + l.Model.P;
            if (t <= 0) continue;
            z += s * l.Model.Z / t; i += s * l.Model.I / t; p += s * l.Model.P / t; w += s;
        }
        return w > 0 ? new ZipModel(z / w, i / w, p / w) : ZipModel.ConstantImpedance;
    }

    /// <summary>
    /// Builds the dynamic system: solves the initial power flow (unless given), creates one unit per in-service generator from
    /// <paramref name="models"/> (GENCLS from <c>Generator</c> data when a generator has no model) and initializes all states.
    /// </summary>
    public static DynamicSystem Build(PowerNetwork network, DynamicModelSet? models = null, DynamicSystemOptions? options = null, PowerFlowResult? powerFlow = null)
    {
        var opt = options ?? DynamicSystemOptions.Default;
        var net = network.Clone();
        var pf = powerFlow ?? PowerFlowSolver.Solve(net, opt.PowerFlow ?? new PowerFlowOptions { Tolerance = 1e-12, MaxIterations = 50 });
        if (!pf.Converged) throw new InvalidOperationException("Initial power flow did not converge.");
        models ??= new DynamicModelSet();
        var warnings = new List<string>(models.Warnings);
        double sb = net.BaseMva, ws = 2 * Math.PI * (net.FrequencyHz > 0 ? net.FrequencyHz : 50);
        var units = new List<DynamicUnit>();
        for (int gi = 0; gi < net.Generators.Count; gi++)
        {
            var g = net.Generators[gi];
            if (!g.InService) continue;
            string name = g.Name ?? $"G{gi + 1}";
            models.Generators.TryGetValue(gi, out var d);
            double mb = g.MBase > 0 ? g.MBase : sb;
            DynamicUnit u;
            if (d?.GridForming is { } gf)
            {
                if (gf.MBase <= 0) gf.MBase = mb;
                gf.OmegaS = ws;
                var gu = new GridFormingUnit(name, g.Bus, gi, gf);
                gu.SetBase(gf.MBase);
                u = gu;
            }
            else if (d?.Converter is { } cv)
            {
                if (cv.MBase <= 0) cv.MBase = mb;
                if (d.PlantControl is Repca rp) rp.OmegaS = ws;
                if (d.Machine is not null) warnings.Add($"{name}: both machine and converter models given; the machine model is ignored.");
                var ru = new RenewableUnit(name, g.Bus, gi, cv, d.ElectricalControl, d.PlantControl);
                ru.SetBase(cv.MBase);
                u = ru;
            }
            else
            {
                var m = d?.Machine;
                if (m is null)
                {
                    m = new Gencls(g.H >= 1e5 ? 0 : g.H, g.D, g.XdPrime);
                    if (d is not null && d.Components.Any()) warnings.Add($"{name}: controllers given without a machine model; GENCLS from generator data used.");
                }
                if (m.MBase <= 0) m.MBase = mb;
                if (double.IsNaN(m.Ra)) m.Ra = g.Ra;
                if (m is Gencls c && double.IsNaN(c.XdPrime)) c.XdPrime = g.XdPrime;
                m.OmegaS = ws;
                var mu = new SynchronousMachineUnit(name, g.Bus, gi, m, d?.Exciter, d?.Governor, d?.Stabilizer);
                mu.SetBase(m.MBase);
                u = mu;
            }
            units.Add(u);
        }
        units.AddRange(models.Loads);
        int off = 0;
        foreach (var u in units)
        {
            u.SystemBase = sb;
            u.BusIndex = net.IndexOf(u.BusId);
            u.StateOffset = off;
            off += u.StateCount;
        }
        return new DynamicSystem(net, pf, units, opt, warnings);
    }

    // ------------------------------------------------------------------ configuration

    /// <summary>Restores the initial configuration (branch status, faults, loads, unit status and references).</summary>
    public void Reset()
    {
        for (int k = 0; k < _branchBase.Length; k++) _net.Branches[k].InService = _branchBase[k];
        _faults.Clear();
        _loadY = (Complex[])_loadY0.Clone(); _loadI = (Complex[])_loadI0.Clone(); _loadP = (Complex[])_loadP0.Clone();
        for (int k = 0; k < Units.Count; k++)
        {
            Units[k].InService = true;
            foreach (var c in Units[k].Components) c.ResetDiscrete();
            foreach (var (n, v) in InitialReferences[k]) Units[k].TrySetReference(n, v);
        }
        Rebuild();
    }

    /// <summary>Applies an event to the configuration (refactorizes the network matrix when needed).</summary>
    public void Apply(DynamicEvent e)
    {
        switch (e.Kind)
        {
            case DynamicEventKind.BusFault:
                _faults[_net.IndexOf(e.BusId)] = e.Impedance == Complex.Zero ? ClassicalModel.BoltedFaultImpedance : e.Impedance;
                break;
            case DynamicEventKind.ClearFault:
                _faults.Remove(_net.IndexOf(e.BusId));
                break;
            case DynamicEventKind.TripBranch: _net.Branches[e.Index].InService = false; break;
            case DynamicEventKind.CloseBranch: _net.Branches[e.Index].InService = true; break;
            case DynamicEventKind.TripGenerator:
                foreach (var u in Units.Where(u => u.GeneratorIndex == e.Index)) u.InService = false;
                break;
            case DynamicEventKind.LoadScale:
                {
                    int i = _net.IndexOf(e.BusId);
                    _loadY[i] *= e.Value; _loadI[i] *= e.Value; _loadP[i] *= e.Value;
                    break;
                }
            case DynamicEventKind.LoadStep:
                {
                    int i = _net.IndexOf(e.BusId);
                    var ds = new Complex(e.DeltaPMw, e.DeltaQMvar) / SystemBase;
                    var zip = LoadFractions(_net, i, Options);
                    double vm = _v0[i];
                    _loadY[i] += Complex.Conjugate(ds * zip.Z) / (vm * vm);
                    _loadI[i] += ds * zip.I; _loadP[i] += ds * zip.P;
                    break;
                }
            case DynamicEventKind.ReferenceStep:
                {
                    var u = UnitOf(e.Index) ?? throw new ArgumentException($"No dynamic unit for generator {e.Index}.");
                    double v = e.Value;
                    if (e.Relative)
                    {
                        if (!u.TryGetReference(e.Reference!, out double cur)) throw new ArgumentException($"{u.Name} has no reference '{e.Reference}'.");
                        v += cur;
                    }
                    if (!u.TrySetReference(e.Reference!, v)) throw new ArgumentException($"{u.Name} has no reference '{e.Reference}'.");
                    return; // no network change
                }
        }
        Rebuild();
    }

    private void Rebuild()
    {
        var y = AdmittanceMatrix.Build(_net).Y;
        int n = BusCount;
        var b = new SparseBuilder<Complex>(n, n);
        for (int i = 0; i < n; i++)
        {
            var idx = y.RowIndices(i); var val = y.RowValues(i);
            for (int q = 0; q < idx.Length; q++) b.Add(i, idx[q], val[q]);
            b.Add(i, i, _loadY[i]);
        }
        foreach (var u in Units) if (u.InService && u.NortonAdmittance != Complex.Zero) b.Add(u.BusIndex, u.BusIndex, u.NortonAdmittance);
        foreach (var (i, z) in _faults) b.Add(i, i, 1 / z);
        _y = b.ToCsr();
        _lu = SparseLU<Complex>.Factorize(_y, _ordering);
        _ordering ??= _lu.Ordering;
        ConfigurationVersion++;
    }

    /// <summary>Incremented whenever the network matrix changes.</summary>
    public int ConfigurationVersion { get; private set; }

    /// <summary>Current network matrix (Y-bus + loads + Norton admittances + faults) [pu].</summary>
    public SparseMatrix<Complex> NetworkMatrix => _y!;

    // ------------------------------------------------------------------ algebraic network

    /// <summary>Static-load current injection at bus i for voltage v (ZIP I and P parts; the Z part is in the matrix).</summary>
    private Complex LoadCurrent(int i, Complex v)
    {
        if (_loadI[i] == Complex.Zero && _loadP[i] == Complex.Zero) return Complex.Zero;
        double vm = v.Magnitude;
        if (vm < 1e-8) return Complex.Zero;
        var sI = _loadI[i] * (vm / _v0[i]);
        double brk = Options.ConstantPowerBreakpoint;
        var sP = vm >= brk ? _loadP[i] : _loadP[i] * (vm * vm / (brk * brk));
        return -Complex.Conjugate((sI + sP) / v);
    }

    /// <summary>Total source current injected at each bus (units + nonlinear loads) for states <paramref name="x"/> and voltages <paramref name="v"/>.</summary>
    internal void Injections(ReadOnlySpan<double> x, ReadOnlySpan<Complex> v, Span<Complex> inj)
    {
        inj.Clear();
        foreach (var u in Units)
            if (u.InService) inj[u.BusIndex] += u.Injection(x.Slice(u.StateOffset, u.StateCount), v[u.BusIndex]);
        for (int i = 0; i < BusCount; i++) inj[i] += LoadCurrent(i, v[i]);
    }

    /// <summary>
    /// Solves the network equations for given states: one sparse solve when all injections are voltage independent, otherwise
    /// fixed-point iterations on the factorized matrix with a Newton fallback. <paramref name="v"/> holds the initial guess and the result.
    /// Returns the number of iterations (−1 when not converged).
    /// </summary>
    public int SolveNetwork(ReadOnlySpan<double> x, Complex[] v, double tolerance = 1e-10, int maxIterations = 50)
    {
        var inj = new Complex[BusCount];
        Injections(x, v, inj);
        var sol = _lu!.Solve(inj);
        if (!_nonlinear) { sol.CopyTo(v, 0); return 1; }
        for (int it = 1; it <= maxIterations; it++)
        {
            double d = 0;
            for (int i = 0; i < BusCount; i++) d = Math.Max(d, (sol[i] - v[i]).Magnitude);
            sol.CopyTo(v, 0);
            if (d < tolerance) return it;
            Injections(x, v, inj);
            sol = _lu.Solve(inj);
            if (it == 12) return NewtonNetwork(x, v, tolerance, 20) is var r && r >= 0 ? it + r : -1;
        }
        return -1;
    }

    /// <summary>Newton iterations on the network equations only (states fixed).</summary>
    private int NewtonNetwork(ReadOnlySpan<double> x, Complex[] v, double tolerance, int maxIterations)
    {
        int n = BusCount;
        var inj = new Complex[n];
        var xs = x.ToArray();
        for (int it = 1; it <= maxIterations; it++)
        {
            var r = NetworkResidual(xs, v, inj);
            double nr = r.Max(Math.Abs);
            if (nr < tolerance) return it;
            var b = new SparseBuilder<double>(2 * n, 2 * n);
            StampNetwork(b, 0, xs, v);
            var lu = SparseLU<double>.Factorize(b.ToCsr());
            var dz = lu.Solve(r);
            for (int i = 0; i < n; i++) v[i] -= new Complex(dz[2 * i], dz[2 * i + 1]);
        }
        return -1;
    }

    /// <summary>Network residual g = I(x, V) − Y·V arranged as [Im g₀, Re g₀, Im g₁, …] (rows swapped so the Jacobian diagonal is ±B).</summary>
    internal double[] NetworkResidual(ReadOnlySpan<double> x, ReadOnlySpan<Complex> v, Complex[] inj, double[]? into = null, int offset = 0)
    {
        Injections(x, v, inj);
        var yv = _y!.Multiply(v);
        var r = into ?? new double[2 * BusCount];
        for (int i = 0; i < BusCount; i++)
        {
            var g = inj[i] - yv[i];
            r[offset + 2 * i] = g.Imaginary;
            r[offset + 2 * i + 1] = g.Real;
        }
        return r;
    }

    /// <summary>
    /// Stamps ∂g/∂V into <paramref name="b"/> at row/column offset <paramref name="o"/>: −Y (exact) plus numerical local derivatives of
    /// voltage-dependent unit and load injections. Unknown order per bus: (Vr, Vi); equation order per bus: (Im g, Re g).
    /// </summary>
    internal void StampNetwork(SparseBuilder<double> b, int o, ReadOnlySpan<double> x, ReadOnlySpan<Complex> v)
    {
        var y = _y!;
        for (int i = 0; i < BusCount; i++)
        {
            var idx = y.RowIndices(i); var val = y.RowValues(i);
            for (int q = 0; q < idx.Length; q++)
            {
                int k = idx[q];
                double g = val[q].Real, bb = val[q].Imaginary;
                // Re(YV) = G·Vr − B·Vi ; Im(YV) = B·Vr + G·Vi ; g = I − YV
                b.Add(o + 2 * i, o + 2 * k, -bb);
                b.Add(o + 2 * i, o + 2 * k + 1, -g);
                b.Add(o + 2 * i + 1, o + 2 * k, -g);
                b.Add(o + 2 * i + 1, o + 2 * k + 1, bb);
            }
        }
        for (int i = 0; i < BusCount; i++)
        {
            bool any = _loadI[i] != Complex.Zero || _loadP[i] != Complex.Zero;
            foreach (int k in _unitsAtBus[i]) any |= Units[k].InService && Units[k].VoltageDependent;
            if (!any) continue;
            var v0 = v[i];
            var i0 = BusInjection(i, x, v0);
            const double h = 1e-7;
            var dr = (BusInjection(i, x, v0 + h) - i0) / h;
            var di = (BusInjection(i, x, v0 + new Complex(0, h)) - i0) / h;
            b.Add(o + 2 * i, o + 2 * i, dr.Imaginary);
            b.Add(o + 2 * i, o + 2 * i + 1, di.Imaginary);
            b.Add(o + 2 * i + 1, o + 2 * i, dr.Real);
            b.Add(o + 2 * i + 1, o + 2 * i + 1, di.Real);
        }
    }

    private Complex BusInjection(int i, ReadOnlySpan<double> x, Complex vv)
    {
        Complex s = LoadCurrent(i, vv);
        foreach (int k in _unitsAtBus[i])
        {
            var u = Units[k];
            if (u.InService && u.VoltageDependent) s += u.Injection(x.Slice(u.StateOffset, u.StateCount), vv);
        }
        return s;
    }

    // ------------------------------------------------------------------ differential part

    /// <summary>State derivatives f(t, x, V) of all units (zero for out-of-service units).</summary>
    public void Derivatives(double t, ReadOnlySpan<double> x, ReadOnlySpan<Complex> v, Span<double> dx)
    {
        foreach (var u in Units)
        {
            var d = dx.Slice(u.StateOffset, u.StateCount);
            if (!u.InService) { d.Clear(); continue; }
            u.Derivatives(t, x.Slice(u.StateOffset, u.StateCount), v[u.BusIndex], d);
        }
    }

    /// <summary>Clamps limited states of all units.</summary>
    public void ApplyLimits(Span<double> x)
    {
        foreach (var u in Units) u.ApplyLimits(x.Slice(u.StateOffset, u.StateCount));
    }

    internal void AfterStep(double t, ReadOnlySpan<double> x)
    {
        foreach (var u in Units) if (u.InService) u.AfterStep(t, x.Slice(u.StateOffset, u.StateCount));
    }

    /// <summary>
    /// Initialization check: max |f(x₀, V₀)| and max network mismatch [pu] at the initial point (both ≈ 0 for a consistent initialization).
    /// </summary>
    public (double MaxDerivative, double MaxMismatch) InitializationResidual()
    {
        var dx = new double[StateCount];
        Derivatives(0, InitialState, InitialVoltages, dx);
        var r = NetworkResidual(InitialState, InitialVoltages, new Complex[BusCount]);
        return (dx.Length == 0 ? 0 : dx.Max(Math.Abs), r.Max(Math.Abs));
    }

    public override string ToString() => $"{_net.Name}: {Units.Count} units, {StateCount} states, {BusCount} buses";
}
