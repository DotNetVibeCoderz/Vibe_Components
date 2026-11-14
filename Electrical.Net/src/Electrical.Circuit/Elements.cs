using System.Numerics;

namespace Electrical.Circuit;

/// <summary>Numerical integration rule for transient companion models.</summary>
public enum IntegrationMethod
{
    /// <summary>Trapezoidal rule (second order, A-stable; the first step after a discontinuous start uses backward Euler).</summary>
    Trapezoidal,
    /// <summary>Backward Euler (first order, L-stable, numerically damped).</summary>
    BackwardEuler,
}

internal enum AnalysisMode { OperatingPoint, Transient }

/// <summary>State passed to element stamps.</summary>
internal sealed class StampContext
{
    public AnalysisMode Mode;
    public double Time;
    public double Dt;
    public IntegrationMethod Method;
    public double[] Guess = [];
    public double Gmin = 1e-12;
}

/// <summary>MNA matrix/right-hand-side accumulator; index −1 is ground and is skipped.</summary>
internal sealed class Stamper<T> where T : INumberBase<T>
{
    public readonly Electrical.Core.Numerics.DenseMatrix<T> A;
    public readonly T[] B;
    public Stamper(int n) { A = new(n, n); B = new T[n]; Array.Fill(B, T.Zero); }

    public void Add(int i, int j, T v) { if (i >= 0 && j >= 0) A[i, j] += v; }
    public void Rhs(int i, T v) { if (i >= 0) B[i] += v; }

    /// <summary>Admittance y between nodes a and b.</summary>
    public void Admittance(int a, int b, T y) { Add(a, a, y); Add(b, b, y); Add(a, b, -y); Add(b, a, -y); }

    /// <summary>Current <paramref name="i"/> flowing out of node a and into node b through an element (i.e. injected into b).</summary>
    public void Current(int a, int b, T i) { Rhs(a, -i); Rhs(b, i); }

    /// <summary>KCL incidence of a branch current flowing from a to b through the element.</summary>
    public void Incidence(int a, int b, int br, T scale)
    {
        Add(a, br, scale); Add(b, br, -scale);
    }

    /// <summary>Branch-equation voltage terms V_a − V_b (times <paramref name="scale"/>).</summary>
    public void BranchVoltage(int br, int a, int b, T scale) { Add(br, a, scale); Add(br, b, -scale); }
}

/// <summary>Base class of circuit elements. Node names are case-insensitive; "0" / "gnd" is ground.</summary>
public abstract class CircuitElement
{
    protected CircuitElement(string name, params string[] nodes)
    {
        Name = name;
        Nodes = nodes;
    }

    public string Name { get; }
    /// <summary>Terminal node names.</summary>
    public IReadOnlyList<string> Nodes { get; }

    internal int[] N = [];
    internal int Branch = -1;
    internal virtual int BranchCount => 0;
    internal virtual bool IsNonlinear => false;

    internal abstract void StampReal(Stamper<double> s, StampContext ctx);
    internal abstract void StampAc(Stamper<Complex> s, double omega);
    /// <summary>Called after a transient step (or the operating point) is accepted; returns the element current [A].</summary>
    internal virtual void Accept(double[] x, StampContext ctx) { }
    /// <summary>Initialises dynamic state for a transient run starting from <paramref name="x"/> (operating point) or initial conditions.</summary>
    internal virtual void InitTransient(double[] x, bool useInitialConditions) { }
    /// <summary>Applies Newton voltage limiting; returns true when the candidate was limited (iteration not converged).</summary>
    internal virtual bool Limit(double[] x) => false;
    /// <summary>Element current [A] (from the first to the second node, through the element) at a real solution.</summary>
    internal abstract double Current(double[] x, StampContext ctx);
    internal abstract Complex CurrentAc(Complex[] x, double omega);

    protected static double V(double[] x, int i) => i < 0 ? 0 : x[i];
    protected static Complex V(Complex[] x, int i) => i < 0 ? Complex.Zero : x[i];
    internal double Vab(double[] x) => V(x, N[0]) - V(x, N[1]);

    public override string ToString() => $"{GetType().Name} {Name} ({string.Join(", ", Nodes)})";
}

/// <summary>Linear resistor [Ω].</summary>
public sealed class Resistor(string name, string a, string b, double ohms) : CircuitElement(name, a, b)
{
    public double Ohms { get; set; } = ohms > 0 ? ohms : throw new ArgumentOutOfRangeException(nameof(ohms));
    internal override void StampReal(Stamper<double> s, StampContext ctx) => s.Admittance(N[0], N[1], 1 / Ohms);
    internal override void StampAc(Stamper<Complex> s, double omega) => s.Admittance(N[0], N[1], 1 / Ohms);
    internal override double Current(double[] x, StampContext ctx) => Vab(x) / Ohms;
    internal override Complex CurrentAc(Complex[] x, double omega) => (V(x, N[0]) - V(x, N[1])) / Ohms;
}

/// <summary>Capacitor [F] with optional initial voltage [V] (used with initial conditions).</summary>
public sealed class Capacitor(string name, string a, string b, double farads, double? initialVoltage = null) : CircuitElement(name, a, b)
{
    public double Farads { get; set; } = farads;
    public double? InitialVoltage { get; set; } = initialVoltage;
    private double _vPrev, _iPrev, _hist, _geq;
    private bool _firstStep;

    internal override void InitTransient(double[] x, bool uic)
    {
        _vPrev = uic ? InitialVoltage ?? 0 : Vab(x);
        _iPrev = 0;
        _firstStep = uic;
    }

    private void Companion(StampContext ctx)
    {
        bool be = ctx.Method == IntegrationMethod.BackwardEuler || _firstStep;
        _geq = (be ? 1 : 2) * Farads / ctx.Dt;
        _hist = be ? _geq * _vPrev : _geq * _vPrev + _iPrev;
    }

    internal override void StampReal(Stamper<double> s, StampContext ctx)
    {
        if (ctx.Mode == AnalysisMode.OperatingPoint) return;
        Companion(ctx);
        s.Admittance(N[0], N[1], _geq);
        s.Current(N[1], N[0], _hist); // history current source injected into node a
    }

    internal override void StampAc(Stamper<Complex> s, double omega) => s.Admittance(N[0], N[1], new Complex(0, omega * Farads));

    internal override double Current(double[] x, StampContext ctx)
    {
        if (ctx.Mode == AnalysisMode.OperatingPoint) return 0;
        Companion(ctx);
        return _geq * Vab(x) - _hist;
    }

    internal override void Accept(double[] x, StampContext ctx)
    {
        if (ctx.Mode == AnalysisMode.OperatingPoint) return;
        _iPrev = Current(x, ctx);
        _vPrev = Vab(x);
        _firstStep = false;
    }

    internal override Complex CurrentAc(Complex[] x, double omega) => (V(x, N[0]) - V(x, N[1])) * new Complex(0, omega * Farads);
}

/// <summary>Inductor [H] with optional initial current [A] (branch-current MNA formulation).</summary>
public sealed class Inductor(string name, string a, string b, double henries, double? initialCurrent = null) : CircuitElement(name, a, b)
{
    public double Henries { get; set; } = henries;
    public double? InitialCurrent { get; set; } = initialCurrent;
    internal double IPrev, VPrev;
    internal bool FirstStep;
    internal override int BranchCount => 1;

    internal double Factor(StampContext ctx) => ctx.Method == IntegrationMethod.BackwardEuler || FirstStep ? 1 / ctx.Dt : 2 / ctx.Dt;
    internal bool Trap(StampContext ctx) => !(ctx.Method == IntegrationMethod.BackwardEuler || FirstStep);

    internal override void InitTransient(double[] x, bool uic)
    {
        IPrev = uic ? InitialCurrent ?? 0 : x[Branch];
        VPrev = 0;
        FirstStep = uic;
    }

    internal override void StampReal(Stamper<double> s, StampContext ctx)
    {
        s.Incidence(N[0], N[1], Branch, 1);
        s.BranchVoltage(Branch, N[0], N[1], 1);
        if (ctx.Mode == AnalysisMode.OperatingPoint) return;
        double f = Factor(ctx);
        s.Add(Branch, Branch, -f * Henries);
        s.Rhs(Branch, -f * Henries * IPrev - (Trap(ctx) ? VPrev : 0));
    }

    internal override void StampAc(Stamper<Complex> s, double omega)
    {
        s.Incidence(N[0], N[1], Branch, 1);
        s.BranchVoltage(Branch, N[0], N[1], 1);
        s.Add(Branch, Branch, new Complex(0, -omega * Henries));
    }

    internal override void Accept(double[] x, StampContext ctx)
    {
        if (ctx.Mode == AnalysisMode.OperatingPoint) return;
        IPrev = x[Branch];
        VPrev = Vab(x);
        FirstStep = false;
    }

    internal override double Current(double[] x, StampContext ctx) => x[Branch];
    internal override Complex CurrentAc(Complex[] x, double omega) => x[Branch];
}

/// <summary>Magnetic coupling between two inductors (SPICE K element), M = k·√(L1·L2).</summary>
public sealed class MutualInductance(string name, string inductor1, string inductor2, double coupling) : CircuitElement(name)
{
    public string Inductor1 { get; } = inductor1;
    public string Inductor2 { get; } = inductor2;
    /// <summary>Coupling coefficient k (0 &lt; k ≤ 1).</summary>
    public double Coupling { get; set; } = coupling is > 0 and <= 1 ? coupling : throw new ArgumentOutOfRangeException(nameof(coupling));
    internal Inductor L1 = null!, L2 = null!;
    public double MutualHenries => Coupling * Math.Sqrt(L1.Henries * L2.Henries);

    internal override void StampReal(Stamper<double> s, StampContext ctx)
    {
        if (ctx.Mode == AnalysisMode.OperatingPoint) return;
        double m = MutualHenries;
        double f1 = L1.Factor(ctx), f2 = L2.Factor(ctx);
        s.Add(L1.Branch, L2.Branch, -f1 * m);
        s.Rhs(L1.Branch, -f1 * m * L2.IPrev);
        s.Add(L2.Branch, L1.Branch, -f2 * m);
        s.Rhs(L2.Branch, -f2 * m * L1.IPrev);
    }

    internal override void StampAc(Stamper<Complex> s, double omega)
    {
        var z = new Complex(0, -omega * MutualHenries);
        s.Add(L1.Branch, L2.Branch, z);
        s.Add(L2.Branch, L1.Branch, z);
    }

    internal override double Current(double[] x, StampContext ctx) => 0;
    internal override Complex CurrentAc(Complex[] x, double omega) => 0;
}

/// <summary>Independent voltage source (V). Branch current flows from + through the source to − (SPICE sign: a delivering source has negative current).</summary>
public sealed class VoltageSource(string name, string plus, string minus, Waveform waveform, double acMagnitude = 0, double acPhaseDeg = 0) : CircuitElement(name, plus, minus)
{
    public Waveform Waveform { get; set; } = waveform;
    /// <summary>AC small-signal magnitude [V] for frequency sweeps.</summary>
    public double AcMagnitude { get; set; } = acMagnitude;
    public double AcPhaseDeg { get; set; } = acPhaseDeg;
    internal override int BranchCount => 1;

    internal override void StampReal(Stamper<double> s, StampContext ctx)
    {
        s.Incidence(N[0], N[1], Branch, 1);
        s.BranchVoltage(Branch, N[0], N[1], 1);
        s.Rhs(Branch, Waveform.Value(ctx.Time));
    }

    internal override void StampAc(Stamper<Complex> s, double omega)
    {
        s.Incidence(N[0], N[1], Branch, 1);
        s.BranchVoltage(Branch, N[0], N[1], 1);
        s.Rhs(Branch, Complex.FromPolarCoordinates(AcMagnitude, AcPhaseDeg * Math.PI / 180));
    }

    internal override double Current(double[] x, StampContext ctx) => x[Branch];
    internal override Complex CurrentAc(Complex[] x, double omega) => x[Branch];
}

/// <summary>Independent current source (I). Current flows from + through the source to − (i.e. it is delivered into the − node).</summary>
public sealed class CurrentSource(string name, string plus, string minus, Waveform waveform, double acMagnitude = 0, double acPhaseDeg = 0) : CircuitElement(name, plus, minus)
{
    public Waveform Waveform { get; set; } = waveform;
    public double AcMagnitude { get; set; } = acMagnitude;
    public double AcPhaseDeg { get; set; } = acPhaseDeg;

    internal override void StampReal(Stamper<double> s, StampContext ctx) => s.Current(N[0], N[1], Waveform.Value(ctx.Time));
    internal override void StampAc(Stamper<Complex> s, double omega) =>
        s.Current(N[0], N[1], Complex.FromPolarCoordinates(AcMagnitude, AcPhaseDeg * Math.PI / 180));
    internal override double Current(double[] x, StampContext ctx) => Waveform.Value(ctx.Time);
    internal override Complex CurrentAc(Complex[] x, double omega) => Complex.FromPolarCoordinates(AcMagnitude, AcPhaseDeg * Math.PI / 180);
}

/// <summary>Voltage-controlled voltage source (SPICE E): V(+,−) = gain·V(c+, c−).</summary>
public sealed class Vcvs(string name, string plus, string minus, string controlPlus, string controlMinus, double gain)
    : CircuitElement(name, plus, minus, controlPlus, controlMinus)
{
    public double Gain { get; set; } = gain;
    internal override int BranchCount => 1;

    private void Stamp<T>(Stamper<T> s) where T : INumberBase<T>
    {
        s.Incidence(N[0], N[1], Branch, T.One);
        s.BranchVoltage(Branch, N[0], N[1], T.One);
        s.BranchVoltage(Branch, N[2], N[3], T.CreateChecked(-Gain));
    }

    internal override void StampReal(Stamper<double> s, StampContext ctx) => Stamp(s);
    internal override void StampAc(Stamper<Complex> s, double omega) => Stamp(s);
    internal override double Current(double[] x, StampContext ctx) => x[Branch];
    internal override Complex CurrentAc(Complex[] x, double omega) => x[Branch];
}

/// <summary>Voltage-controlled current source (SPICE G): current gm·V(c+, c−) flows from + through the source to −.</summary>
public sealed class Vccs(string name, string plus, string minus, string controlPlus, string controlMinus, double transconductance)
    : CircuitElement(name, plus, minus, controlPlus, controlMinus)
{
    /// <summary>Transconductance [S].</summary>
    public double Transconductance { get; set; } = transconductance;

    private void Stamp<T>(Stamper<T> s) where T : INumberBase<T>
    {
        var g = T.CreateChecked(Transconductance);
        s.Add(N[0], N[2], g); s.Add(N[0], N[3], -g);
        s.Add(N[1], N[2], -g); s.Add(N[1], N[3], g);
    }

    internal override void StampReal(Stamper<double> s, StampContext ctx) => Stamp(s);
    internal override void StampAc(Stamper<Complex> s, double omega) => Stamp(s);
    internal override double Current(double[] x, StampContext ctx) => Transconductance * (V(x, N[2]) - V(x, N[3]));
    internal override Complex CurrentAc(Complex[] x, double omega) => Transconductance * (V(x, N[2]) - V(x, N[3]));
}

/// <summary>Time-controlled ideal switch modelled as R_on / R_off.</summary>
public sealed class Switch : CircuitElement
{
    public Switch(string name, string a, string b, Func<double, bool> isClosed, double onResistance = 1e-3, double offResistance = 1e9) : base(name, a, b)
    {
        IsClosed = isClosed; OnResistance = onResistance; OffResistance = offResistance;
    }

    /// <summary>State as a function of time [s] (the operating point uses t = 0).</summary>
    public Func<double, bool> IsClosed { get; set; }
    public double OnResistance { get; set; }
    public double OffResistance { get; set; }

    private double G(double t) => 1 / (IsClosed(t) ? OnResistance : OffResistance);
    internal override void StampReal(Stamper<double> s, StampContext ctx) => s.Admittance(N[0], N[1], G(ctx.Time));
    internal override void StampAc(Stamper<Complex> s, double omega) => s.Admittance(N[0], N[1], G(0));
    internal override double Current(double[] x, StampContext ctx) => Vab(x) * G(ctx.Time);
    internal override Complex CurrentAc(Complex[] x, double omega) => (V(x, N[0]) - V(x, N[1])) * G(0);
}

/// <summary>Junction diode with the Shockley equation I = I_s·(e^(V/(n·V_T)) − 1), solved by Newton iterations with SPICE-style junction limiting.</summary>
public sealed class Diode(string name, string anode, string cathode, double saturationCurrent = 1e-14, double emissionCoefficient = 1.0,
    double seriesResistance = 0, double thermalVoltage = 0.025852) : CircuitElement(name, anode, cathode)
{
    /// <summary>Saturation current I_s [A].</summary>
    public double SaturationCurrent { get; set; } = saturationCurrent;
    /// <summary>Emission coefficient n.</summary>
    public double EmissionCoefficient { get; set; } = emissionCoefficient;
    /// <summary>Thermal voltage V_T [V] (25.85 mV at 300 K).</summary>
    public double ThermalVoltage { get; set; } = thermalVoltage;
    /// <summary>Unused placeholder for series resistance [Ω] (model a separate resistor if needed).</summary>
    public double SeriesResistance { get; set; } = seriesResistance;
    internal override bool IsNonlinear => true;
    private double _vd; // junction voltage of the current Newton iterate (after limiting)
    private double _gdAc;

    private double NVt => EmissionCoefficient * ThermalVoltage;
    private double VCrit => NVt * Math.Log(NVt / (Math.Sqrt(2) * SaturationCurrent));

    private (double I, double G) Eval(double v)
    {
        double e = Math.Exp(Math.Min(v / NVt, 80));
        return (SaturationCurrent * (e - 1), SaturationCurrent * e / NVt);
    }

    internal override void StampReal(Stamper<double> s, StampContext ctx)
    {
        var (i, g) = Eval(_vd);
        g += ctx.Gmin;
        double ieq = i - g * _vd;
        s.Admittance(N[0], N[1], g);
        s.Current(N[0], N[1], ieq);
    }

    internal override bool Limit(double[] x)
    {
        double vnew = Vab(x), vold = _vd;
        double vt = NVt;
        bool limited = false;
        if (vnew > VCrit && Math.Abs(vnew - vold) > 2 * vt)
        {
            if (vold > 0)
            {
                double arg = 1 + (vnew - vold) / vt;
                vnew = arg > 0 ? vold + vt * Math.Log(arg) : VCrit;
            }
            else vnew = vt * Math.Log(vnew / vt);
            limited = true;
        }
        _vd = vnew;
        return limited;
    }

    internal override void Accept(double[] x, StampContext ctx)
    {
        _vd = Vab(x);
        _gdAc = Eval(_vd).G + ctx.Gmin;
    }

    internal override void StampAc(Stamper<Complex> s, double omega) => s.Admittance(N[0], N[1], _gdAc);
    internal override double Current(double[] x, StampContext ctx) => Eval(Vab(x)).I;
    internal override Complex CurrentAc(Complex[] x, double omega) => (V(x, N[0]) - V(x, N[1])) * _gdAc;
}

/// <summary>Ideal transformer with turns ratio n = N_p/N_s: V_p = n·V_s, I_s = −n·I_p (dot at p1 and s1).</summary>
public sealed class IdealTransformer(string name, string primaryPlus, string primaryMinus, string secondaryPlus, string secondaryMinus, double turnsRatio)
    : CircuitElement(name, primaryPlus, primaryMinus, secondaryPlus, secondaryMinus)
{
    public double TurnsRatio { get; set; } = turnsRatio;
    internal override int BranchCount => 1;

    private void Stamp<T>(Stamper<T> s) where T : INumberBase<T>
    {
        var n = T.CreateChecked(TurnsRatio);
        s.Incidence(N[0], N[1], Branch, T.One);
        s.Incidence(N[2], N[3], Branch, -n);
        s.BranchVoltage(Branch, N[0], N[1], T.One);
        s.BranchVoltage(Branch, N[2], N[3], -n);
    }

    internal override void StampReal(Stamper<double> s, StampContext ctx) => Stamp(s);
    internal override void StampAc(Stamper<Complex> s, double omega) => Stamp(s);
    /// <summary>Primary current [A].</summary>
    internal override double Current(double[] x, StampContext ctx) => x[Branch];
    internal override Complex CurrentAc(Complex[] x, double omega) => x[Branch];
}
