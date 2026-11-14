using System.Numerics;

namespace Electrical.Dynamics;

/// <summary>
/// GENCLS: classical machine, constant EMF E′ behind Ra + jX′d (X′d and Ra taken from <c>Generator</c> unless set).
/// States: δ [rad], Δω [pu]. H = 0 makes an infinite bus (constant angle and speed).
/// </summary>
public sealed class Gencls : MachineModel
{
    private double _e;

    /// <summary>PSS/E CON order: H, D.</summary>
    public static readonly string[] ParameterNames = ["H", "D"];

    /// <param name="h">Inertia constant [s on MBase] (0 = infinite bus).</param>
    /// <param name="d">Damping [pu on MBase].</param>
    /// <param name="xdPrime">Transient reactance [pu on MBase] (NaN = take <c>Generator.XdPrime</c>).</param>
    public Gencls(double h, double d, double xdPrime = double.NaN)
    {
        H = h; D = d; XdPrime = xdPrime;
        AddState("delta"); AddState("dw");
    }

    public static Gencls FromDyr(ReadOnlySpan<double> c) => new(c[0], c.Length > 1 ? c[1] : 0);

    /// <summary>Transient reactance [pu on MBase] (NaN = take <c>Generator.XdPrime</c>).</summary>
    public double XdPrime { get; set; }
    public override string ModelName => "GENCLS";
    public override double H { get; }
    public override double D { get; }
    public override int AngleState => 0;
    public override int SpeedState => 1;
    public override Complex SourceImpedance => new(double.IsNaN(Ra) ? 0 : Ra, XdPrime);
    /// <summary>Internal EMF magnitude [pu] (set at initialization).</summary>
    public double InternalEmf => _e;

    public override Complex InternalVoltage(ReadOnlySpan<double> x) => Complex.FromPolarCoordinates(_e, x[0]);

    public override void ComputeSignals(ReadOnlySpan<double> x, Complex v, Complex i, MachineSignals s)
    {
        var e = InternalVoltage(x);
        s.V = v; s.I = i; s.Vt = v.Magnitude;
        var sv = v * Complex.Conjugate(i);
        s.P = sv.Real; s.Q = sv.Imaginary;
        s.Te = (e * Complex.Conjugate(i)).Real;
        s.SpeedDeviation = x[1]; s.Angle = x[0];
        (s.Id, s.Iq) = ToRotor(i, x[0]);
        s.XadIfd = _e;
    }

    public override void Derivatives(ReadOnlySpan<double> x, MachineSignals s, double efd, double pm, Span<double> dx)
    {
        if (H <= 0) { dx[0] = 0; dx[1] = 0; return; }
        dx[0] = OmegaS * x[1];
        dx[1] = SpeedDerivative(x[1], pm, s.Te);
    }

    public override void Initialize(Complex v, Complex i, Span<double> x, out double efd, out double pm)
    {
        var e = v + SourceImpedance * i;
        _e = e.Magnitude;
        x[0] = e.Phase; x[1] = 0;
        pm = (e * Complex.Conjugate(i)).Real;
        efd = _e;
    }
}

/// <summary>
/// GENROU: round-rotor machine, PSS/E formulation (Sauer–Pai sixth-order equations with X″d = X″q, quadratic saturation applied to both
/// axes from |ψ″|, stator transients and speed voltages neglected). States: δ, Δω, E′q, E′d, ψkd, ψkq. Parameters in pu on MBase.
/// </summary>
public sealed class Genrou : MachineModel
{
    /// <summary>PSS/E CON order.</summary>
    public static readonly string[] ParameterNames = ["T'do", "T''do", "T'qo", "T''qo", "H", "D", "Xd", "Xq", "X'd", "X'q", "X''d", "Xl", "S(1.0)", "S(1.2)"];

    private readonly QuadraticSaturation _sat;
    private readonly double _gd1, _gq1, _gd2, _gq2, _gqd;

    public double TdoPrime { get; }
    public double TdoSubtransient { get; }
    public double TqoPrime { get; }
    public double TqoSubtransient { get; }
    public override double H { get; }
    public override double D { get; }
    public double Xd { get; }
    public double Xq { get; }
    public double XdPrime { get; }
    public double XqPrime { get; }
    public double XdSubtransient { get; }
    public double Xl { get; }
    public double S10 { get; }
    public double S12 { get; }

    public Genrou(double tdoPrime, double tdoSubtransient, double tqoPrime, double tqoSubtransient, double h, double d,
        double xd, double xq, double xdPrime, double xqPrime, double xdSubtransient, double xl, double s10 = 0, double s12 = 0)
    {
        TdoPrime = tdoPrime; TdoSubtransient = tdoSubtransient; TqoPrime = tqoPrime; TqoSubtransient = tqoSubtransient;
        H = h; D = d; Xd = xd; Xq = xq; XdPrime = xdPrime; XqPrime = xqPrime; XdSubtransient = xdSubtransient; Xl = xl; S10 = s10; S12 = s12;
        if (!(xdSubtransient < xdPrime && xdSubtransient < xqPrime && xl < xdSubtransient))
            throw new ArgumentException("GENROU requires Xl < X''d < X'd and X''d < X'q.");
        _sat = new QuadraticSaturation(s10, s12);
        _gd1 = (xdSubtransient - xl) / (xdPrime - xl);
        _gq1 = (xdSubtransient - xl) / (xqPrime - xl);
        _gd2 = (xdPrime - xdSubtransient) / ((xdPrime - xl) * (xdPrime - xl));
        _gq2 = (xqPrime - xdSubtransient) / ((xqPrime - xl) * (xqPrime - xl));
        _gqd = (xq - xl) / (xd - xl);
        AddState("delta"); AddState("dw"); AddState("Eq'"); AddState("Ed'"); AddState("psikd"); AddState("psikq");
    }

    public static Genrou FromDyr(ReadOnlySpan<double> c) =>
        new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c.Length > 12 ? c[12] : 0, c.Length > 13 ? c[13] : 0);

    public override string ModelName => "GENROU";
    public override int AngleState => 0;
    public override int SpeedState => 1;
    public override Complex SourceImpedance => new(double.IsNaN(Ra) ? 0 : Ra, XdSubtransient);

    private (double pd, double pq) Flux(ReadOnlySpan<double> x) =>
        (_gd1 * x[2] + (1 - _gd1) * x[4], -_gq1 * x[3] + (1 - _gq1) * x[5]);

    public override Complex InternalVoltage(ReadOnlySpan<double> x)
    {
        var (pd, pq) = Flux(x);
        return ToNetwork(-pq, pd, x[0]);
    }

    public override void ComputeSignals(ReadOnlySpan<double> x, Complex v, Complex i, MachineSignals s)
    {
        var (pd, pq) = Flux(x);
        s.V = v; s.I = i; s.Vt = v.Magnitude;
        var sv = v * Complex.Conjugate(i);
        s.P = sv.Real; s.Q = sv.Imaginary;
        (s.Id, s.Iq) = ToRotor(i, x[0]);
        s.Te = pd * s.Iq - pq * s.Id;
        s.SpeedDeviation = x[1]; s.Angle = x[0];
        double psi = Math.Sqrt(pd * pd + pq * pq);
        double se = psi > 1e-9 ? _sat.Evaluate(psi) / psi : 0;
        s.XadIfd = x[2] + (Xd - XdPrime) * (s.Id - _gd2 * (x[4] + (XdPrime - Xl) * s.Id - x[2])) + se * pd;
    }

    public override void Derivatives(ReadOnlySpan<double> x, MachineSignals s, double efd, double pm, Span<double> dx)
    {
        var (pd, pq) = Flux(x);
        double psi = Math.Sqrt(pd * pd + pq * pq);
        double se = psi > 1e-9 ? _sat.Evaluate(psi) / psi : 0;
        double id = s.Id, iq = s.Iq;
        dx[0] = H <= 0 ? 0 : OmegaS * x[1];
        dx[1] = SpeedDerivative(x[1], pm, s.Te);
        dx[2] = (efd - s.XadIfd) / TdoPrime;
        dx[3] = (-x[3] + (Xq - XqPrime) * (iq - _gq2 * (x[5] + (XqPrime - Xl) * iq + x[3])) + _gqd * se * pq) / TqoPrime;
        dx[4] = (-x[4] + x[2] - (XdPrime - Xl) * id) / TdoSubtransient;
        dx[5] = (-x[5] - x[3] - (XqPrime - Xl) * iq) / TqoSubtransient;
    }

    public override void Initialize(Complex v, Complex i, Span<double> x, out double efd, out double pm)
    {
        double ra = SourceImpedance.Real;
        var eq = v + new Complex(ra, Xq) * i;
        double delta = eq.Phase;
        var (vd, vq) = ToRotor(v, delta);
        var (id, iq) = ToRotor(i, delta);
        x[0] = delta; x[1] = 0;
        x[3] = (Xq - XqPrime) * iq;
        x[5] = -x[3] - (XqPrime - Xl) * iq;
        x[2] = vq + ra * iq + XdPrime * id;
        x[4] = x[2] - (XdPrime - Xl) * id;
        efd = x[2] + (Xd - XdPrime) * id;
        pm = (v * Complex.Conjugate(i)).Real + ra * (i.Magnitude * i.Magnitude);
        RefineEquilibrium(v, i, x, ref efd, ref pm);
    }
}

/// <summary>
/// GENSAL: salient-pole machine, PSS/E formulation (no q-axis transient circuit, one q-axis damper, saturation Sat(E′q) on the d axis).
/// States: δ, Δω, E′q, ψkd, ψ″q. Parameters in pu on MBase.
/// </summary>
public sealed class Gensal : MachineModel
{
    /// <summary>PSS/E CON order.</summary>
    public static readonly string[] ParameterNames = ["T'do", "T''do", "T''qo", "H", "D", "Xd", "Xq", "X'd", "X''d", "Xl", "S(1.0)", "S(1.2)"];

    private readonly QuadraticSaturation _sat;
    private readonly double _gd1, _gd2;

    public double TdoPrime { get; }
    public double TdoSubtransient { get; }
    public double TqoSubtransient { get; }
    public override double H { get; }
    public override double D { get; }
    public double Xd { get; }
    public double Xq { get; }
    public double XdPrime { get; }
    public double XdSubtransient { get; }
    public double Xl { get; }

    public Gensal(double tdoPrime, double tdoSubtransient, double tqoSubtransient, double h, double d, double xd, double xq, double xdPrime,
        double xdSubtransient, double xl, double s10 = 0, double s12 = 0)
    {
        TdoPrime = tdoPrime; TdoSubtransient = tdoSubtransient; TqoSubtransient = tqoSubtransient; H = h; D = d;
        Xd = xd; Xq = xq; XdPrime = xdPrime; XdSubtransient = xdSubtransient; Xl = xl;
        if (!(xdSubtransient < xdPrime && xl < xdSubtransient && xdSubtransient < xq))
            throw new ArgumentException("GENSAL requires Xl < X''d < X'd and X''d < Xq.");
        _sat = new QuadraticSaturation(s10, s12);
        _gd1 = (xdSubtransient - xl) / (xdPrime - xl);
        _gd2 = (xdPrime - xdSubtransient) / ((xdPrime - xl) * (xdPrime - xl));
        AddState("delta"); AddState("dw"); AddState("Eq'"); AddState("psikd"); AddState("psi''q");
    }

    public static Gensal FromDyr(ReadOnlySpan<double> c) =>
        new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c.Length > 10 ? c[10] : 0, c.Length > 11 ? c[11] : 0);

    public override string ModelName => "GENSAL";
    public override int AngleState => 0;
    public override int SpeedState => 1;
    public override Complex SourceImpedance => new(double.IsNaN(Ra) ? 0 : Ra, XdSubtransient);

    public override Complex InternalVoltage(ReadOnlySpan<double> x)
    {
        double pd = _gd1 * x[2] + (1 - _gd1) * x[3];
        return ToNetwork(-x[4], pd, x[0]);
    }

    public override void ComputeSignals(ReadOnlySpan<double> x, Complex v, Complex i, MachineSignals s)
    {
        double pd = _gd1 * x[2] + (1 - _gd1) * x[3], pq = x[4];
        s.V = v; s.I = i; s.Vt = v.Magnitude;
        var sv = v * Complex.Conjugate(i);
        s.P = sv.Real; s.Q = sv.Imaginary;
        (s.Id, s.Iq) = ToRotor(i, x[0]);
        s.Te = pd * s.Iq - pq * s.Id;
        s.SpeedDeviation = x[1]; s.Angle = x[0];
        s.XadIfd = x[2] + (Xd - XdPrime) * (s.Id - _gd2 * (x[3] + (XdPrime - Xl) * s.Id - x[2])) + _sat.Evaluate(x[2]);
    }

    public override void Derivatives(ReadOnlySpan<double> x, MachineSignals s, double efd, double pm, Span<double> dx)
    {
        dx[0] = H <= 0 ? 0 : OmegaS * x[1];
        dx[1] = SpeedDerivative(x[1], pm, s.Te);
        dx[2] = (efd - s.XadIfd) / TdoPrime;
        dx[3] = (-x[3] + x[2] - (XdPrime - Xl) * s.Id) / TdoSubtransient;
        dx[4] = (-x[4] - (Xq - XdSubtransient) * s.Iq) / TqoSubtransient;
    }

    public override void Initialize(Complex v, Complex i, Span<double> x, out double efd, out double pm)
    {
        double ra = SourceImpedance.Real;
        double delta = (v + new Complex(ra, Xq) * i).Phase;
        var (vd, vq) = ToRotor(v, delta);
        var (id, iq) = ToRotor(i, delta);
        x[0] = delta; x[1] = 0;
        x[2] = vq + ra * iq + XdPrime * id;
        x[3] = x[2] - (XdPrime - Xl) * id;
        x[4] = -(Xq - XdSubtransient) * iq;
        efd = x[2] + (Xd - XdPrime) * id + _sat.Evaluate(x[2]);
        pm = (v * Complex.Conjugate(i)).Real + ra * (i.Magnitude * i.Magnitude);
        RefineEquilibrium(v, i, x, ref efd, ref pm);
    }
}
