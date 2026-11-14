namespace Electrical.Dynamics;

/// <summary>
/// TGOV1: steam turbine-governor. Valve 1/(1+sT1) on (Pref − Δω)/R with non-windup [VMIN, VMAX], reheater (1+sT2)/(1+sT3),
/// Pm = output − Dt·Δω. CONs: R, T1, VMAX, VMIN, T2, T3, Dt (pu on MBase, s).
/// </summary>
public sealed class Tgov1 : GovernorModel
{
    public static readonly string[] ParameterNames = ["R", "T1", "VMAX", "VMIN", "T2", "T3", "Dt"];
    private readonly int _x1;
    private readonly LeadLag _ll;
    public double R { get; } public double T1 { get; } public double Vmax { get; } public double Vmin { get; }
    public double T2 { get; } public double T3 { get; } public double Dt { get; }

    public Tgov1(double r, double t1, double vmax, double vmin, double t2, double t3, double dt)
    {
        R = r; T1 = t1; Vmax = vmax; Vmin = vmin; T2 = t2; T3 = t3; Dt = dt;
        _x1 = AddStateIf(Blocks.Active(t1), "valve");
        _ll = new LeadLag(AddStateIf(Blocks.Active(t3), "reheat"), t2, t3);
    }

    public static Tgov1 FromDyr(ReadOnlySpan<double> c) => new(c[0], c[1], c[2], c[3], c[4], c[5], c[6]);
    public override string ModelName => "TGOV1";

    public override void Initialize(double pm0, MachineSignals s, Span<double> x)
    {
        if (_x1 >= 0) x[_x1] = pm0;
        _ll.Initialize(x, pm0);
        Pref = R * pm0;
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double dw = s.SpeedDeviation;
        double u = (Pref - dw) / R;
        double v;
        if (_x1 >= 0) { v = x[_x1]; dx[_x1] = Blocks.NonWindupLag(v, u, 1, T1, Vmin, Vmax); }
        else v = Blocks.Clamp(u, Vmin, Vmax);
        _ll.Derivative(x, v, dx);
        return _ll.Output(x, v) - Dt * dw;
    }

    public override void ApplyLimits(Span<double> x) => Clamp(x, _x1, Vmin, Vmax);
}

/// <summary>
/// HYGOV: hydro turbine-governor with transient droop and non-elastic water column. States: speed-error filter e (Tf), gate integrator c
/// (rate limit VELM, position [GMIN, GMAX]), gate servo g (Tg), water flow q (TW). Desired gate = c + e/r; head h = (q/g)²;
/// Pm = At·h·(q − qNL) − Dturb·g·Δω. Simplification: the velocity limit acts on the integral part only.
/// CONs: R, r, Tr, Tf, Tg, VELM, GMAX, GMIN, TW, At, Dturb, qNL.
/// </summary>
public sealed class Hygov : GovernorModel
{
    public static readonly string[] ParameterNames = ["R", "r", "Tr", "Tf", "Tg", "VELM", "GMAX", "GMIN", "TW", "At", "Dturb", "qNL"];
    private readonly int _e, _c, _g, _q;
    public double R { get; } public double TransientDroop { get; } public double Tr { get; } public double Tf { get; } public double Tg { get; }
    public double Velm { get; } public double Gmax { get; } public double Gmin { get; } public double Tw { get; } public double At { get; }
    public double Dturb { get; } public double QNl { get; }

    public Hygov(double r, double transientDroop, double tr, double tf, double tg, double velm, double gmax, double gmin, double tw, double at,
        double dturb, double qnl)
    {
        R = r; TransientDroop = transientDroop; Tr = tr; Tf = tf; Tg = tg; Velm = velm; Gmax = gmax; Gmin = gmin; Tw = tw; At = at; Dturb = dturb; QNl = qnl;
        if (!Blocks.Active(tr) || !Blocks.Active(tw) || transientDroop <= 0) throw new ArgumentException("HYGOV requires r, Tr, TW > 0.");
        _e = AddStateIf(Blocks.Active(tf), "e");
        _c = AddState("c");
        _g = AddStateIf(Blocks.Active(tg), "gate");
        _q = AddState("q");
    }

    public static Hygov FromDyr(ReadOnlySpan<double> c) => new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11]);
    public override string ModelName => "HYGOV";

    public override void Initialize(double pm0, MachineSignals s, Span<double> x)
    {
        double q = pm0 / At + QNl;
        if (_e >= 0) x[_e] = 0;
        x[_c] = q;
        if (_g >= 0) x[_g] = q;
        x[_q] = q;
        Pref = R * q;
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double dw = s.SpeedDeviation;
        double cdes0 = x[_c];
        // the droop loop uses the integral part to stay explicit when Tf = 0
        double err = Pref - dw - R * cdes0;
        double e = _e >= 0 ? x[_e] : err;
        if (_e >= 0) dx[_e] = (err - e) / Tf;
        double cdes = Blocks.Clamp(x[_c] + e / TransientDroop, Gmin, Gmax);
        dx[_c] = Blocks.NonWindupIntegrator(x[_c], Blocks.Clamp(e / (TransientDroop * Tr), -Velm, Velm), Gmin, Gmax);
        double g = _g >= 0 ? x[_g] : cdes;
        if (_g >= 0) dx[_g] = (cdes - g) / Tg;
        double q = x[_q];
        double gg = Math.Max(g, 1e-4);
        double h = q / gg * (q / gg);
        dx[_q] = (1 - h) / Tw;
        return At * h * (q - QNl) - Dturb * g * dw;
    }

    public override void ApplyLimits(Span<double> x) => Clamp(x, _c, Gmin, Gmax);
}

/// <summary>
/// GAST: gas turbine. Low-value select of the governor (Pref − Δω)/R and the load limit AT + KT·(AT − x3); fuel valve 1/(1+sT1) with
/// non-windup [VMIN, VMAX], turbine 1/(1+sT2), exhaust sensor 1/(1+sT3); Pm = x2 − DTURB·Δω. CONs: R, T1, T2, T3, AT, KT, VMAX, VMIN, DTURB.
/// </summary>
public sealed class Gast : GovernorModel
{
    public static readonly string[] ParameterNames = ["R", "T1", "T2", "T3", "AT", "KT", "VMAX", "VMIN", "DTURB"];
    private readonly int _x1, _x2, _x3;
    public double R { get; } public double T1 { get; } public double T2 { get; } public double T3 { get; } public double LoadLimit { get; }
    public double Kt { get; } public double Vmax { get; } public double Vmin { get; } public double Dturb { get; }

    public Gast(double r, double t1, double t2, double t3, double at, double kt, double vmax, double vmin, double dturb)
    {
        R = r; T1 = t1; T2 = t2; T3 = t3; LoadLimit = at; Kt = kt; Vmax = vmax; Vmin = vmin; Dturb = dturb;
        _x1 = AddStateIf(Blocks.Active(t1), "fuel");
        _x2 = AddStateIf(Blocks.Active(t2), "turbine");
        _x3 = AddStateIf(Blocks.Active(t3), "exhaust");
    }

    public static Gast FromDyr(ReadOnlySpan<double> c) => new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8]);
    public override string ModelName => "GAST";

    public override void Initialize(double pm0, MachineSignals s, Span<double> x)
    {
        if (_x1 >= 0) x[_x1] = pm0;
        if (_x2 >= 0) x[_x2] = pm0;
        if (_x3 >= 0) x[_x3] = pm0;
        Pref = R * pm0;
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double dw = s.SpeedDeviation;
        double x3 = _x3 >= 0 ? x[_x3] : double.NaN;
        double gov = (Pref - dw) / R;
        double u = gov;
        if (!double.IsNaN(x3)) u = Math.Min(gov, LoadLimit + Kt * (LoadLimit - x3));
        double x1;
        if (_x1 >= 0) { x1 = x[_x1]; dx[_x1] = Blocks.NonWindupLag(x1, u, 1, T1, Vmin, Vmax); }
        else x1 = Blocks.Clamp(u, Vmin, Vmax);
        double x2 = _x2 >= 0 ? x[_x2] : x1;
        if (_x2 >= 0) dx[_x2] = (x1 - x2) / T2;
        if (_x3 >= 0) dx[_x3] = (x2 - x3) / T3;
        return x2 - Dturb * dw;
    }

    public override void ApplyLimits(Span<double> x) => Clamp(x, _x1, Vmin, Vmax);
}

/// <summary>
/// IEEEG1: IEEE type 1 steam turbine-governor, simplified to a single shaft (high- and low-pressure fractions K1…K8 all act on this
/// machine). Speed relay K·(1+sT2)/(1+sT1), valve servo 1/T3 with rate limits [UC, UO] and position [PMIN, PMAX], four steam lags T4…T7.
/// ICONs: JBUS, M (ignored). CONs: K, T1, T2, T3, UO, UC, PMAX, PMIN, T4, K1, K2, T5, K3, K4, T6, K5, K6, T7, K7, K8.
/// </summary>
public sealed class Ieeeg1 : GovernorModel
{
    public static readonly string[] ParameterNames = ["JBUS", "M", "K", "T1", "T2", "T3", "UO", "UC", "PMAX", "PMIN", "T4", "K1", "K2", "T5", "K3", "K4", "T6", "K5", "K6", "T7", "K7", "K8"];
    private readonly int _ll, _gv;
    private readonly int[] _lags = new int[4];
    private readonly double[] _t = new double[4];
    private readonly double[] _k = new double[4];
    public double K { get; } public double T1 { get; } public double T2 { get; } public double T3 { get; } public double Uo { get; } public double Uc { get; }
    public double Pmax { get; } public double Pmin { get; }

    public Ieeeg1(double k, double t1, double t2, double t3, double uo, double uc, double pmax, double pmin, double t4, double k1, double k2,
        double t5, double k3, double k4, double t6, double k5, double k6, double t7, double k7, double k8)
    {
        K = k; T1 = t1; T2 = t2; T3 = t3; Uo = uo; Uc = uc; Pmax = pmax; Pmin = pmin;
        if (!Blocks.Active(t3)) throw new ArgumentException("IEEEG1 requires T3 > 0.");
        _t[0] = t4; _t[1] = t5; _t[2] = t6; _t[3] = t7;
        _k[0] = k1 + k2; _k[1] = k3 + k4; _k[2] = k5 + k6; _k[3] = k7 + k8;
        _ll = AddStateIf(Blocks.Active(t1), "relay");
        _gv = AddState("valve");
        for (int i = 0; i < 4; i++) _lags[i] = AddStateIf(Blocks.Active(_t[i]), $"steam{i + 4}");
    }

    public static Ieeeg1 FromDyr(ReadOnlySpan<double> c) => new(c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13],
        c[14], c[15], c[16], c[17], c[18], c[19], c[20], c[21]);
    public override string ModelName => "IEEEG1";

    private double SumK => _k.Sum() is var s && s > 0 ? s : 1;

    public override void Initialize(double pm0, MachineSignals s, Span<double> x)
    {
        double gv = pm0 / SumK;
        if (_ll >= 0) x[_ll] = 0;
        x[_gv] = gv;
        foreach (int i in _lags) if (i >= 0) x[i] = gv;
        Pref = gv;
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double dw = s.SpeedDeviation;
        double relay = _ll >= 0 ? K * Blocks.LeadLagOutput(x[_ll], dw, T2, T1) : K * dw;
        if (_ll >= 0) dx[_ll] = (dw - x[_ll]) / T1;
        double rate = Blocks.Clamp((Pref - relay - x[_gv]) / T3, Uc, Uo);
        dx[_gv] = Blocks.NonWindupIntegrator(x[_gv], rate, Pmin, Pmax);
        double u = x[_gv], pm = 0;
        for (int i = 0; i < 4; i++)
        {
            double y = _lags[i] >= 0 ? x[_lags[i]] : u;
            if (_lags[i] >= 0) dx[_lags[i]] = (u - y) / _t[i];
            pm += _k[i] * y;
            u = y;
        }
        return pm;
    }

    public override void ApplyLimits(Span<double> x) => Clamp(x, _gv, Pmin, Pmax);
}

/// <summary>
/// IEEEST: IEEE standard stabilizer. Filter (1 + A5·s + A6·s²)/((1 + A1·s + A2·s²)(1 + A3·s + A4·s²)), two lead-lags T1/T2 and T3/T4,
/// washout KS·sT5/(1 + sT6) (KS only when T6 = 0), output limited to [LSMIN, LSMAX]; output zero when VCU/VCL (non-zero) are violated.
/// ICONs: input code (<see cref="StabilizerInput"/>), remote bus (ignored). CONs: A1…A6, T1…T6, KS, LSMAX, LSMIN, VCU, VCL.
/// </summary>
public sealed class Ieeest : StabilizerModel
{
    public static readonly string[] ParameterNames = ["ICS", "IB", "A1", "A2", "A3", "A4", "A5", "A6", "T1", "T2", "T3", "T4", "T5", "T6", "KS", "LSMAX", "LSMIN", "VCU", "VCL"];
    private readonly RationalChain _chain;

    public StabilizerInput Input { get; }
    public double Lsmax { get; } public double Lsmin { get; } public double Vcu { get; } public double Vcl { get; }

    public Ieeest(StabilizerInput input, double a1, double a2, double a3, double a4, double a5, double a6, double t1, double t2, double t3, double t4,
        double t5, double t6, double ks, double lsmax, double lsmin, double vcu = 0, double vcl = 0)
    {
        Input = input; Lsmax = lsmax; Lsmin = lsmin; Vcu = vcu; Vcl = vcl;
        var blocks = new List<RationalBlock>
        {
            new([1, a5, a6], RationalBlock.Multiply([1, a1, a2], [1, a3, a4])),
            Blocks.Active(t2) ? RationalBlock.LeadLag(t1, t2) : new([1], [1]),
            Blocks.Active(t4) ? RationalBlock.LeadLag(t3, t4) : new([1], [1]),
            Blocks.Active(t6) && Blocks.Active(t5) ? new([0, ks * t5], [1, t6]) : new([ks], [1]),
        };
        _chain = new RationalChain([.. blocks], 0);
        for (int i = 0; i < _chain.StateCount; i++) AddState($"x{i + 1}");
    }

    /// <param name="ks">Gain.</param>
    /// <summary>Kundur-style speed-input stabilizer K·sTw/(1+sTw)·(1+sT1)/(1+sT2)·(1+sT3)/(1+sT4) expressed as IEEEST.</summary>
    public static Ieeest LeadLagSpeed(double ks, double tw, double t1, double t2, double t3, double t4, double limit = 0.2) =>
        new(StabilizerInput.SpeedDeviation, 0, 0, 0, 0, 0, 0, t1, t2, t3, t4, tw, tw, ks, limit, -limit);

    public static Ieeest FromDyr(ReadOnlySpan<double> c) => new((StabilizerInput)(int)c[0], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10],
        c[11], c[12], c[13], c[14], c[15], c[16], c.Length > 17 ? c[17] : 0, c.Length > 18 ? c[18] : 0);
    public override string ModelName => "IEEEST";

    public override void Initialize(MachineSignals s, Span<double> x) => _chain.Initialize(Signal(Input, s), x);

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double y = Blocks.Clamp(_chain.Evaluate(x, Signal(Input, s), dx), Lsmin, Lsmax);
        if ((Vcu > 0 && s.Vt > Vcu) || (Vcl > 0 && s.Vt < Vcl)) return 0;
        return y;
    }
}

/// <summary>
/// PSS2B (IEEE 421.5 dual-input, integral-of-accelerating-power stabilizer; PSS2A when T10 = T11 = 0). Input 1 (normally speed) through
/// washouts TW1, TW2 and lag T6; input 2 (normally electrical power) through washouts TW3, TW4 and KS2/(1+sT7); ramp-tracking filter
/// ((1+sT8)/(1+sT9)^M)^N; KS1 and three lead-lags; output limited to [VSTMIN, VSTMAX].
/// ICONs: input-1 code, bus, input-2 code, bus, M, N. CONs: TW1, TW2, T6, TW3, TW4, T7, KS2, KS3, T8, T9, KS1, T1, T2, T3, T4, T10, T11,
/// VS1MAX, VS1MIN, VS2MAX, VS2MIN, VSTMAX, VSTMIN (PSS2A: …, T4, VSTMAX, VSTMIN).
/// </summary>
public sealed class Pss2b : StabilizerModel
{
    public static readonly string[] ParameterNames = ["J1", "K1", "J2", "K2", "M", "N", "TW1", "TW2", "T6", "TW3", "TW4", "T7", "KS2", "KS3", "T8", "T9", "KS1",
        "T1", "T2", "T3", "T4", "T10", "T11", "VS1MAX", "VS1MIN", "VS2MAX", "VS2MIN", "VSTMAX", "VSTMIN"];
    private readonly RationalChain _p1, _p2, _ramp, _lead;
    private readonly double _ks3, _ks1;

    public StabilizerInput Input1 { get; }
    public StabilizerInput Input2 { get; }
    public double Vs1max { get; init; } = double.PositiveInfinity;
    public double Vs1min { get; init; } = double.NegativeInfinity;
    public double Vs2max { get; init; } = double.PositiveInfinity;
    public double Vs2min { get; init; } = double.NegativeInfinity;
    public double Vstmax { get; }
    public double Vstmin { get; }

    public Pss2b(StabilizerInput input1, StabilizerInput input2, int m, int n, double tw1, double tw2, double t6, double tw3, double tw4, double t7,
        double ks2, double ks3, double t8, double t9, double ks1, double t1, double t2, double t3, double t4, double t10, double t11,
        double vstmax, double vstmin)
    {
        Input1 = input1; Input2 = input2; _ks3 = ks3; _ks1 = ks1; Vstmax = vstmax; Vstmin = vstmin;
        static RationalBlock W(double t) => RationalBlock.Washout(1, t);
        static RationalBlock L(double k, double t) => Blocks.Active(t) ? RationalBlock.Lag(k, t) : new([k], [1]);
        static RationalBlock LL(double a, double b) => Blocks.Active(b) ? RationalBlock.LeadLag(a, b) : new([1], [1]);
        int o = 0;
        _p1 = new RationalChain([W(tw1), W(tw2), L(1, t6)], o); o += _p1.StateCount;
        _p2 = new RationalChain([W(tw3), W(tw4), L(ks2, t7)], o); o += _p2.StateCount;
        m = Math.Max(m, 0); n = Math.Max(n, 0);
        var ramp = Blocks.Active(t9) && m > 0 && n > 0
            ? new RationalBlock(RationalBlock.Power([1, t8], n), RationalBlock.Power([1, t9], m * n))
            : new RationalBlock([1], [1]);
        _ramp = new RationalChain([ramp], o); o += _ramp.StateCount;
        _lead = new RationalChain([LL(t1, t2), LL(t3, t4), LL(t10, t11)], o); o += _lead.StateCount;
        for (int i = 0; i < o; i++) AddState($"x{i + 1}");
    }

    /// <summary>Creates PSS2B from PSS/E values (ICONs first). 29 values = PSS2B, 23 values = PSS2A layout.</summary>
    public static Pss2b FromDyr(ReadOnlySpan<double> c)
    {
        bool a = c.Length < 29;
        double t10 = a ? 0 : c[21], t11 = a ? 0 : c[22];
        double vstmax = a ? c[21] : c[27], vstmin = a ? c[22] : c[28];
        var p = new Pss2b((StabilizerInput)(int)c[0], (StabilizerInput)(int)c[2], (int)c[4], (int)c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12],
            c[13], c[14], c[15], c[16], c[17], c[18], c[19], c[20], t10, t11, vstmax, vstmin)
        {
            Vs1max = a ? double.PositiveInfinity : c[23], Vs1min = a ? double.NegativeInfinity : c[24],
            Vs2max = a ? double.PositiveInfinity : c[25], Vs2min = a ? double.NegativeInfinity : c[26],
        };
        return p;
    }

    public override string ModelName => "PSS2B";

    private (double u1, double u2) Inputs(MachineSignals s) =>
        (Blocks.Clamp(Signal(Input1, s), Vs1min, Vs1max), Blocks.Clamp(Signal(Input2, s), Vs2min, Vs2max));

    public override void Initialize(MachineSignals s, Span<double> x)
    {
        var (u1, u2) = Inputs(s);
        double y1 = _p1.Initialize(u1, x), y2 = _p2.Initialize(u2, x);
        double r = _ramp.Initialize(y1 + _ks3 * y2, x);
        _lead.Initialize(_ks1 * (r - y2), x);
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        var (u1, u2) = Inputs(s);
        double y1 = _p1.Evaluate(x, u1, dx), y2 = _p2.Evaluate(x, u2, dx);
        double r = _ramp.Evaluate(x, y1 + _ks3 * y2, dx);
        return Blocks.Clamp(_lead.Evaluate(x, _ks1 * (r - y2), dx), Vstmin, Vstmax);
    }
}
