using System.Numerics;

namespace Electrical.Dynamics;

/// <summary>Quantities of a unit after its last evaluation (system-base pu for P/Q; NaN when not applicable).</summary>
public sealed class UnitOutputs
{
    /// <summary>Rotor / internal angle in the network frame [rad].</summary>
    public double AngleRad = double.NaN;
    /// <summary>Speed [pu].</summary>
    public double Speed = double.NaN;
    /// <summary>Terminal voltage magnitude [pu].</summary>
    public double Vt;
    /// <summary>Active / reactive power injected into the network [pu on the system base].</summary>
    public double P, Q;
    /// <summary>Field voltage [pu].</summary>
    public double Efd = double.NaN;
    /// <summary>Mechanical power [pu on MBase].</summary>
    public double Pm = double.NaN;
    /// <summary>Converter active / reactive current [pu on MBase].</summary>
    public double Ip = double.NaN, Iq = double.NaN;
}

/// <summary>
/// A dynamic device connected to one bus. The network sees it as a Norton equivalent: a constant admittance
/// <see cref="NortonAdmittance"/> (stamped into the Y matrix) in parallel with a source current <see cref="Injection"/>,
/// both in pu on the system base in the synchronously rotating network frame.
/// </summary>
public abstract class DynamicUnit
{
    protected DynamicUnit(string name, int busId, int generatorIndex)
    {
        Name = name; BusId = busId; GeneratorIndex = generatorIndex;
    }

    public string Name { get; }
    public int BusId { get; }
    /// <summary>Index in <c>PowerNetwork.Generators</c> (−1 for load units).</summary>
    public int GeneratorIndex { get; }
    /// <summary>Offset of the unit's states in the global state vector.</summary>
    public int StateOffset { get; internal set; }
    public bool InService { get; internal set; } = true;
    /// <summary>Unit MVA base.</summary>
    public double MBase { get; protected set; } = 100;
    /// <summary>System MVA base.</summary>
    public double SystemBase { get; internal set; } = 100;
    internal int BusIndex { get; set; }

    public abstract IReadOnlyList<DynamicComponent> Components { get; }
    public int StateCount => Components.Sum(c => c.StateCount);

    /// <summary>Qualified state names "unit.MODEL.state".</summary>
    public IEnumerable<string> StateNames => Components.SelectMany(c => c.StateNames.Select(s => $"{Name}.{c.ModelName}.{s}"));

    /// <summary>Constant Norton admittance [pu on the system base].</summary>
    public virtual Complex NortonAdmittance => Complex.Zero;
    /// <summary>True when <see cref="Injection"/> depends on the terminal voltage (requires network iterations).</summary>
    public virtual bool VoltageDependent => false;
    /// <summary>Inertia weight H·MBase [MW·s] for the centre of inertia (0 = excluded).</summary>
    public virtual double InertiaWeight => 0;
    /// <summary>True for an infinite bus (fixed angle reference).</summary>
    public virtual bool IsInfiniteBus => false;

    public UnitOutputs Outputs { get; } = new();

    /// <summary>Initializes states for terminal voltage <paramref name="v"/> and injected power <paramref name="s"/> [pu on the system base].</summary>
    public abstract void Initialize(Complex v, Complex s, Span<double> x);
    /// <summary>Source current [pu on the system base].</summary>
    public abstract Complex Injection(ReadOnlySpan<double> x, Complex v);
    /// <summary>State derivatives; also refreshes <see cref="Outputs"/>.</summary>
    public abstract void Derivatives(double t, ReadOnlySpan<double> x, Complex v, Span<double> dx);

    public virtual void ApplyLimits(Span<double> x)
    {
        int o = 0;
        foreach (var c in Components) { c.ApplyLimits(x.Slice(o, c.StateCount)); o += c.StateCount; }
    }

    public virtual void AfterStep(double t, ReadOnlySpan<double> x)
    {
        int o = 0;
        foreach (var c in Components) { c.AfterStep(t, x.Slice(o, c.StateCount)); o += c.StateCount; }
    }

    /// <summary>Sets a reference on the first component that knows it (e.g. "Vref", "Pref", "Qref", "Efd", "Pm").</summary>
    public virtual bool TrySetReference(string name, double value) => Components.Any(c => c.TrySetReference(name, value));

    public virtual bool TryGetReference(string name, out double value)
    {
        foreach (var c in Components) if (c.TryGetReference(name, out value)) return true;
        value = double.NaN; return false;
    }

    protected static int Offset(IReadOnlyList<DynamicComponent> cs, DynamicComponent? c)
    {
        int o = 0;
        foreach (var k in cs) { if (ReferenceEquals(k, c)) return o; o += k.StateCount; }
        return -1;
    }

    public override string ToString() => $"{Name} @ bus {BusId}: {string.Join(" + ", Components.Select(c => c.ModelName))}";
}

/// <summary>Synchronous machine with optional exciter, governor and stabilizer.</summary>
public sealed class SynchronousMachineUnit : DynamicUnit
{
    private readonly DynamicComponent[] _cs;
    private readonly int _oe, _og, _os;
    private readonly MachineSignals _s = new();
    private double _efd0, _pm0;

    public SynchronousMachineUnit(string name, int busId, int generatorIndex, MachineModel machine, ExciterModel? exciter = null,
        GovernorModel? governor = null, StabilizerModel? stabilizer = null) : base(name, busId, generatorIndex)
    {
        Machine = machine; Exciter = exciter; Governor = governor; Stabilizer = stabilizer;
        _cs = new DynamicComponent?[] { machine, exciter, governor, stabilizer }.Where(c => c is not null).ToArray()!;
        _oe = Offset(_cs, exciter); _og = Offset(_cs, governor); _os = Offset(_cs, stabilizer);
        MBase = machine.MBase > 0 ? machine.MBase : 100;
    }

    public MachineModel Machine { get; }
    public ExciterModel? Exciter { get; }
    public GovernorModel? Governor { get; }
    public StabilizerModel? Stabilizer { get; }
    public override IReadOnlyList<DynamicComponent> Components => _cs;
    /// <summary>Signals after the last evaluation (machine base).</summary>
    public MachineSignals Signals => _s;

    /// <summary>Constant field voltage used without exciter [pu].</summary>
    public double FieldVoltage => _efd0;
    /// <summary>Constant mechanical power used without governor [pu on MBase].</summary>
    public double MechanicalPower => _pm0;

    private double Scale => MBase / SystemBase;
    public override Complex NortonAdmittance => InService ? 1 / Machine.SourceImpedance * Scale : Complex.Zero;
    public override double InertiaWeight => Machine.H * MBase;
    public override bool IsInfiniteBus => Machine.H <= 0;

    internal void SetBase(double mbase) => MBase = mbase;

    public override void Initialize(Complex v, Complex s, Span<double> x)
    {
        foreach (var c in _cs) c.ResetDiscrete();
        var i = Complex.Conjugate(s / v) / Scale;
        var xm = x[..Machine.StateCount];
        Machine.Initialize(v, i, xm, out _efd0, out _pm0);
        Machine.ComputeSignals(xm, v, i, _s);
        _s.Efd = _efd0; _s.Pm = _pm0; _s.Vs = 0;
        if (Stabilizer is not null) { Stabilizer.Initialize(_s, x.Slice(_os, Stabilizer.StateCount)); _s.Vs = Stabilizer.Evaluate(x.Slice(_os, Stabilizer.StateCount), _s, []); }
        Exciter?.Initialize(_efd0, _s, x.Slice(_oe, Exciter.StateCount));
        Governor?.Initialize(_pm0, _s, x.Slice(_og, Governor.StateCount));
    }

    public override Complex Injection(ReadOnlySpan<double> x, Complex v) =>
        InService ? Machine.InternalVoltage(x[..Machine.StateCount]) / Machine.SourceImpedance * Scale : Complex.Zero;

    public override void Derivatives(double t, ReadOnlySpan<double> x, Complex v, Span<double> dx)
    {
        var xm = x[..Machine.StateCount];
        var e = Machine.InternalVoltage(xm);
        var i = (e - v) / Machine.SourceImpedance;
        Machine.ComputeSignals(xm, v, i, _s);
        double pm = Governor is null ? _pm0 : Governor.Evaluate(x.Slice(_og, Governor.StateCount), _s, dx.Slice(_og, Governor.StateCount));
        _s.Pm = pm;
        _s.Vs = Stabilizer is null ? 0 : Stabilizer.Evaluate(x.Slice(_os, Stabilizer.StateCount), _s, dx.Slice(_os, Stabilizer.StateCount));
        double efd = Exciter is null ? _efd0 : Exciter.Evaluate(x.Slice(_oe, Exciter.StateCount), _s, dx.Slice(_oe, Exciter.StateCount));
        _s.Efd = efd;
        Machine.Derivatives(xm, _s, efd, pm, dx[..Machine.StateCount]);
        var o = Outputs;
        o.AngleRad = _s.Angle; o.Speed = 1 + _s.SpeedDeviation; o.Vt = _s.Vt; o.P = _s.P * Scale; o.Q = _s.Q * Scale; o.Efd = efd; o.Pm = pm;
    }

    public override bool TrySetReference(string name, double value)
    {
        if (Exciter is null && name.Equals("Efd", StringComparison.OrdinalIgnoreCase)) { _efd0 = value; return true; }
        if (Governor is null && name.Equals("Pm", StringComparison.OrdinalIgnoreCase)) { _pm0 = value; return true; }
        return base.TrySetReference(name, value);
    }

    public override bool TryGetReference(string name, out double value)
    {
        if (Exciter is null && name.Equals("Efd", StringComparison.OrdinalIgnoreCase)) { value = _efd0; return true; }
        if (Governor is null && name.Equals("Pm", StringComparison.OrdinalIgnoreCase)) { value = _pm0; return true; }
        return base.TryGetReference(name, out value);
    }
}

/// <summary>Inverter-based resource: REGC_A converter with optional REEC_A electrical control and REPC_A plant controller.</summary>
public sealed class RenewableUnit : DynamicUnit
{
    private readonly DynamicComponent[] _cs;
    private readonly int _oe, _op;
    private readonly ConverterSignals _s = new();
    private double _ip0, _iq0;

    public RenewableUnit(string name, int busId, int generatorIndex, ConverterModel converter, ElectricalControlModel? electrical = null,
        PlantControlModel? plant = null) : base(name, busId, generatorIndex)
    {
        Converter = converter; ElectricalControl = electrical; PlantControl = plant;
        _cs = new DynamicComponent?[] { converter, electrical, plant }.Where(c => c is not null).ToArray()!;
        _oe = Offset(_cs, electrical); _op = Offset(_cs, plant);
        MBase = converter.MBase > 0 ? converter.MBase : 100;
    }

    public ConverterModel Converter { get; }
    public ElectricalControlModel? ElectricalControl { get; }
    public PlantControlModel? PlantControl { get; }
    public ConverterSignals Signals => _s;
    public override IReadOnlyList<DynamicComponent> Components => _cs;
    public override bool VoltageDependent => true;
    private double Scale => MBase / SystemBase;

    internal void SetBase(double mbase) => MBase = mbase;

    public override void Initialize(Complex v, Complex s, Span<double> x)
    {
        foreach (var c in _cs) c.ResetDiscrete();
        _s.V = v; _s.Vt = v.Magnitude;
        _s.P0 = _s.P = s.Real / Scale; _s.Q0 = _s.Q = s.Imaginary / Scale;
        Converter.Initialize(_s, x[..Converter.StateCount]);
        _ip0 = _s.Ipcmd; _iq0 = _s.Iqcmd;
        _s.Pext = _s.P0; _s.Qext = _s.Q0;
        PlantControl?.Initialize(_s, x.Slice(_op, PlantControl.StateCount));
        ElectricalControl?.Initialize(_s, x.Slice(_oe, ElectricalControl.StateCount));
    }

    private Complex Current(ReadOnlySpan<double> x, Complex v)
    {
        _s.V = v; _s.Vt = v.Magnitude;
        Converter.Output(x[..Converter.StateCount], _s);
        return new Complex(_s.Ip, -_s.Iq) * Complex.FromPolarCoordinates(1, v.Phase);
    }

    public override Complex Injection(ReadOnlySpan<double> x, Complex v) => InService ? Current(x, v) * Scale : Complex.Zero;

    public override void Derivatives(double t, ReadOnlySpan<double> x, Complex v, Span<double> dx)
    {
        var i = Current(x, v);
        var sv = v * Complex.Conjugate(i);
        _s.P = sv.Real; _s.Q = sv.Imaginary;
        if (PlantControl is not null) PlantControl.Evaluate(x.Slice(_op, PlantControl.StateCount), _s, dx.Slice(_op, PlantControl.StateCount));
        else if (ElectricalControl is Reeca r) { _s.Pext = r.Pref; _s.Qext = r.Qref; }
        if (ElectricalControl is not null) ElectricalControl.Evaluate(t, x.Slice(_oe, ElectricalControl.StateCount), _s, dx.Slice(_oe, ElectricalControl.StateCount));
        else { _s.Ipcmd = _ip0; _s.Iqcmd = _iq0; }
        Converter.Derivatives(x[..Converter.StateCount], _s, dx[..Converter.StateCount]);
        var o = Outputs;
        o.Vt = _s.Vt; o.P = _s.P * Scale; o.Q = _s.Q * Scale; o.Ip = _s.Ip; o.Iq = _s.Iq;
        o.Speed = 1 + _s.FrequencyDeviation;
    }

    public override bool TrySetReference(string name, double value)
    {
        if (ElectricalControl is null && name.Equals("Ipcmd", StringComparison.OrdinalIgnoreCase)) { _ip0 = value; return true; }
        if (ElectricalControl is null && name.Equals("Iqcmd", StringComparison.OrdinalIgnoreCase)) { _iq0 = value; return true; }
        // plant controller first so that "Pref"/"Qref" go to the outermost loop
        if (PlantControl is not null && PlantControl.TrySetReference(name, value)) return true;
        return base.TrySetReference(name, value);
    }

    public override bool TryGetReference(string name, out double value)
    {
        if (PlantControl is not null && PlantControl.TryGetReference(name, out value)) return true;
        return base.TryGetReference(name, out value);
    }
}

/// <summary>Grid-forming inverter unit (voltage source behind XF, Norton form).</summary>
public sealed class GridFormingUnit : DynamicUnit
{
    private readonly DynamicComponent[] _cs;

    public GridFormingUnit(string name, int busId, int generatorIndex, GridFormingInverter inverter) : base(name, busId, generatorIndex)
    {
        Inverter = inverter; _cs = [inverter];
        MBase = inverter.MBase > 0 ? inverter.MBase : 100;
    }

    public GridFormingInverter Inverter { get; }
    public override IReadOnlyList<DynamicComponent> Components => _cs;
    public override bool VoltageDependent => true;
    private double Scale => MBase / SystemBase;
    private Complex Yf => 1 / new Complex(0, Inverter.Xf);
    public override Complex NortonAdmittance => InService ? Yf * Scale : Complex.Zero;
    /// <summary>True while the inner current limit is active (after the last evaluation).</summary>
    public bool CurrentLimited { get; private set; }

    internal void SetBase(double mbase) => MBase = mbase;

    public override void Initialize(Complex v, Complex s, Span<double> x) => Inverter.Initialize(v, s / Scale, x);

    public override Complex Injection(ReadOnlySpan<double> x, Complex v) =>
        InService ? (Inverter.Current(x, v, out _) + Yf * v) * Scale : Complex.Zero;

    public override void Derivatives(double t, ReadOnlySpan<double> x, Complex v, Span<double> dx)
    {
        var i = Inverter.Current(x, v, out bool lim);
        CurrentLimited = lim;
        var sv = v * Complex.Conjugate(i);
        Inverter.Derivatives(x, sv.Real, sv.Imaginary, dx);
        var o = Outputs;
        o.AngleRad = x[Inverter.AngleState]; o.Speed = Inverter.Speed(x); o.Vt = v.Magnitude; o.P = sv.Real * Scale; o.Q = sv.Imaginary * Scale;
        o.Ip = (i * Complex.Conjugate(v) / Math.Max(v.Magnitude, 1e-9)).Real;
        o.Iq = -(i * Complex.Conjugate(v) / Math.Max(v.Magnitude, 1e-9)).Imaginary;
    }
}

/// <summary>
/// Exponential-recovery dynamic load (Karlsson–Hill): Tp·dxp/dt = P0·((V/V0)^αs − (V/V0)^αt) − xp, P = xp + P0·(V/V0)^αt, and the same
/// for Q with Tq, βs, βt. It takes <see cref="Fraction"/> of the static load at its bus (that share is removed from the static model).
/// </summary>
public sealed class ExponentialRecoveryLoad : DynamicUnit
{
    private readonly LoadComponent _c;
    private double _p0, _q0, _v0 = 1;

    /// <param name="tp">Active power recovery time constant [s].</param>
    /// <param name="tq">Reactive power recovery time constant [s].</param>
    /// <param name="alphaS">Steady-state active voltage exponent.</param>
    /// <param name="alphaT">Transient active voltage exponent.</param>
    /// <param name="betaS">Steady-state reactive voltage exponent.</param>
    /// <param name="betaT">Transient reactive voltage exponent.</param>
    /// <param name="fraction">Share of the bus load represented dynamically (0…1).</param>
    public ExponentialRecoveryLoad(int busId, double tp = 5, double tq = 5, double alphaS = 0, double alphaT = 2, double betaS = 0, double betaT = 2,
        double fraction = 1) : base($"LOAD{busId}", busId, -1)
    {
        Tp = tp; Tq = tq; AlphaS = alphaS; AlphaT = alphaT; BetaS = betaS; BetaT = betaT; Fraction = fraction;
        _c = new LoadComponent();
    }

    public double Tp { get; } public double Tq { get; } public double AlphaS { get; } public double AlphaT { get; }
    public double BetaS { get; } public double BetaT { get; } public double Fraction { get; }
    public override IReadOnlyList<DynamicComponent> Components => [_c];
    public override bool VoltageDependent => true;

    private sealed class LoadComponent : DynamicComponent
    {
        public LoadComponent() { AddState("xp"); AddState("xq"); }
        public override string ModelName => "EXPRL";
        public override ModelCategory Category => ModelCategory.Load;
    }

    /// <param name="s">Consumed power of the dynamic share [pu on the system base] (sign: consumption positive).</param>
    public override void Initialize(Complex v, Complex s, Span<double> x)
    {
        _p0 = s.Real; _q0 = s.Imaginary; _v0 = v.Magnitude; x[0] = 0; x[1] = 0;
    }

    private Complex Demand(ReadOnlySpan<double> x, double vm)
    {
        double r = vm / _v0;
        return new Complex(x[0] + _p0 * Math.Pow(r, AlphaT), x[1] + _q0 * Math.Pow(r, BetaT));
    }

    public override Complex Injection(ReadOnlySpan<double> x, Complex v)
    {
        if (!InService || v.Magnitude < 1e-6) return Complex.Zero;
        return -Complex.Conjugate(Demand(x, v.Magnitude) / v);
    }

    public override void Derivatives(double t, ReadOnlySpan<double> x, Complex v, Span<double> dx)
    {
        double r = v.Magnitude / _v0;
        dx[0] = (_p0 * (Math.Pow(r, AlphaS) - Math.Pow(r, AlphaT)) - x[0]) / Tp;
        dx[1] = (_q0 * (Math.Pow(r, BetaS) - Math.Pow(r, BetaT)) - x[1]) / Tq;
        var d = Demand(x, v.Magnitude);
        Outputs.Vt = v.Magnitude; Outputs.P = -d.Real; Outputs.Q = -d.Imaginary;
    }
}
