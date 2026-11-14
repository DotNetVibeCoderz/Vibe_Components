namespace Electrical.Dynamics;

/// <summary>Series chain of rational blocks sharing a contiguous range of a component's states.</summary>
internal sealed class RationalChain
{
    private readonly RationalBlock[] _blocks;
    private readonly int[] _offsets;

    public RationalChain(RationalBlock[] blocks, int offset)
    {
        _blocks = blocks;
        _offsets = new int[blocks.Length];
        int o = offset;
        for (int i = 0; i < blocks.Length; i++) { _offsets[i] = o; o += blocks[i].Order; }
        StateCount = o - offset;
    }

    public int StateCount { get; }

    public double DcGain => _blocks.Aggregate(1.0, (g, b) => g * (double.IsNaN(b.DcGain) ? 0 : b.DcGain));

    public double Initialize(double u, Span<double> x)
    {
        for (int i = 0; i < _blocks.Length; i++)
        {
            var b = _blocks[i];
            var xs = x.Slice(_offsets[i], b.Order);
            b.Initialize(u, xs);
            u = b.Output(xs, u);
        }
        return u;
    }

    public double Evaluate(ReadOnlySpan<double> x, double u, Span<double> dx)
    {
        for (int i = 0; i < _blocks.Length; i++)
        {
            var b = _blocks[i];
            int o = _offsets[i];
            if (dx.Length > 0) b.Derivatives(x.Slice(o, b.Order), u, dx.Slice(o, b.Order));
            u = b.Output(x.Slice(o, b.Order), u);
        }
        return u;
    }
}

/// <summary>Lead-lag (1 + sTC)/(1 + sTB) stored in one optional state (bypassed when TB = 0).</summary>
internal readonly struct LeadLag(int state, double tc, double tb)
{
    public int State { get; } = state;
    public double Output(ReadOnlySpan<double> x, double u) => State < 0 ? u : Blocks.LeadLagOutput(x[State], u, tc, tb);
    public void Derivative(ReadOnlySpan<double> x, double u, Span<double> dx) { if (State >= 0) dx[State] = (u - x[State]) / tb; }
    public void Initialize(Span<double> x, double u) { if (State >= 0) x[State] = u; }
}

/// <summary>
/// SEXS: simplified excitation system. Efd = K/(1+sTE)·(1+sTA)/(1+sTB)·(Vref − Vt + Vs), output limited to [EMIN, EMAX] (non-windup).
/// CONs: TA/TB, TB, K, TE, EMIN, EMAX.
/// </summary>
public sealed class Sexs : ExciterModel
{
    public static readonly string[] ParameterNames = ["TA/TB", "TB", "K", "TE", "EMIN", "EMAX"];
    private readonly LeadLag _ll;
    private readonly int _efd;

    public double TaOverTb { get; }
    public double Tb { get; }
    public double K { get; }
    public double Te { get; }
    public double Emin { get; }
    public double Emax { get; }

    public Sexs(double taOverTb, double tb, double k, double te, double emin, double emax)
    {
        TaOverTb = taOverTb; Tb = tb; K = k; Te = te; Emin = emin; Emax = emax;
        _ll = new LeadLag(AddStateIf(Blocks.Active(tb), "LL"), taOverTb * tb, tb);
        _efd = AddStateIf(Blocks.Active(te), "Efd");
    }

    public static Sexs FromDyr(ReadOnlySpan<double> c) => new(c[0], c[1], c[2], c[3], c[4], c[5]);
    public override string ModelName => "SEXS";

    public override void Initialize(double efd0, MachineSignals s, Span<double> x)
    {
        double u = efd0 / K;
        _ll.Initialize(x, u);
        if (_efd >= 0) x[_efd] = efd0;
        Vref = s.Vt + u - s.Vs;
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double u = Vref - s.Vt + s.Vs;
        _ll.Derivative(x, u, dx);
        double y = _ll.Output(x, u);
        if (_efd < 0) return Blocks.Clamp(K * y, Emin, Emax);
        dx[_efd] = Blocks.NonWindupLag(x[_efd], y, K, Te, Emin, Emax);
        return x[_efd];
    }

    public override void ApplyLimits(Span<double> x) => Clamp(x, _efd, Emin, Emax);
}

/// <summary>
/// IEEET1: IEEE type 1 DC exciter (1968). States Vm (TR), VR (TA, non-windup [VRMIN, VRMAX]), Efd (TE), rate feedback (TF).
/// KE = 0 is replaced at initialization by the value that makes VR = 0 at the operating point (PSS/E convention). SWITCH is ignored.
/// CONs: TR, KA, TA, VRMAX, VRMIN, KE, TE, KF, TF, SWITCH, E1, SE(E1), E2, SE(E2).
/// </summary>
public sealed class Ieeet1 : ExciterModel
{
    public static readonly string[] ParameterNames = ["TR", "KA", "TA", "VRMAX", "VRMIN", "KE", "TE", "KF", "TF", "SWITCH", "E1", "SE(E1)", "E2", "SE(E2)"];
    private readonly int _vm, _vr, _efd, _vf;
    private readonly ExciterSaturation _se;
    private double _ke;

    public double Tr { get; } public double Ka { get; } public double Ta { get; } public double Vrmax { get; } public double Vrmin { get; }
    public double Ke { get; } public double Te { get; } public double Kf { get; } public double Tf { get; }

    public Ieeet1(double tr, double ka, double ta, double vrmax, double vrmin, double ke, double te, double kf, double tf,
        double e1 = 0, double se1 = 0, double e2 = 0, double se2 = 0)
    {
        Tr = tr; Ka = ka; Ta = ta; Vrmax = vrmax; Vrmin = vrmin; Ke = ke; Te = te; Kf = kf; Tf = tf; _ke = ke;
        if (!Blocks.Active(te)) throw new ArgumentException("IEEET1 requires TE > 0.");
        _se = new ExciterSaturation(e1, se1, e2, se2);
        _vm = AddStateIf(Blocks.Active(tr), "Vm");
        _vr = AddStateIf(Blocks.Active(ta), "VR");
        _efd = AddState("Efd");
        _vf = AddStateIf(kf != 0 && Blocks.Active(tf), "Vf");
    }

    public static Ieeet1 FromDyr(ReadOnlySpan<double> c) => new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8],
        c.Length > 10 ? c[10] : 0, c.Length > 11 ? c[11] : 0, c.Length > 12 ? c[12] : 0, c.Length > 13 ? c[13] : 0);
    public override string ModelName => "IEEET1";

    public override void Initialize(double efd0, MachineSignals s, Span<double> x)
    {
        double sat = _se.Evaluate(efd0);
        if (Ke == 0) _ke = -sat / efd0; // VR = 0
        double vr = _ke * efd0 + sat;
        if (_vm >= 0) x[_vm] = s.Vt;
        if (_vr >= 0) x[_vr] = vr;
        x[_efd] = efd0;
        if (_vf >= 0) x[_vf] = efd0;
        Vref = s.Vt + vr / Ka - s.Vs;
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double vm = _vm >= 0 ? x[_vm] : s.Vt;
        if (_vm >= 0) dx[_vm] = (s.Vt - vm) / Tr;
        double efd = x[_efd];
        double fb = _vf >= 0 ? Kf / Tf * (efd - x[_vf]) : 0;
        if (_vf >= 0) dx[_vf] = (efd - x[_vf]) / Tf;
        double u = Vref + s.Vs - vm - fb;
        double vr;
        if (_vr >= 0) { vr = x[_vr]; dx[_vr] = Blocks.NonWindupLag(vr, u, Ka, Ta, Vrmin, Vrmax); }
        else vr = Blocks.Clamp(Ka * u, Vrmin, Vrmax);
        dx[_efd] = (vr - _ke * efd - _se.Evaluate(efd)) / Te;
        return efd;
    }

    public override void ApplyLimits(Span<double> x) => Clamp(x, _vr, Vrmin, Vrmax);
}

/// <summary>
/// ESST1A: IEEE 421.5 type ST1A static exciter (potential-source controlled rectifier). Vi limited to [VIMIN, VIMAX], two lead-lags,
/// KA/(1+sTA) with non-windup [VAMIN, VAMAX], field-current limiter KLR·(Ifd − ILR), output limits Vt·VRMIN ≤ Efd ≤ Vt·VRMAX − KC·Ifd,
/// rate feedback KF·s/(1+sTF). Under-excitation limiter input (UEL) is not modelled. ICONs: UEL, VOS (1 = PSS at voltage reference,
/// 2 = PSS at the regulator output). CONs: TR, VIMAX, VIMIN, TC, TB, TC1, TB1, KA, TA, VAMAX, VAMIN, VRMAX, VRMIN, KC, KF, TF, KLR, ILR.
/// </summary>
public sealed class Esst1a : ExciterModel
{
    public static readonly string[] ParameterNames = ["UEL", "VOS", "TR", "VIMAX", "VIMIN", "TC", "TB", "TC1", "TB1", "KA", "TA", "VAMAX", "VAMIN", "VRMAX", "VRMIN", "KC", "KF", "TF", "KLR", "ILR"];
    private readonly int _vm, _va, _vf;
    private readonly LeadLag _ll1, _ll2;

    public int Vos { get; }
    public double Tr { get; } public double Vimax { get; } public double Vimin { get; } public double Tc { get; } public double Tb { get; }
    public double Tc1 { get; } public double Tb1 { get; } public double Ka { get; } public double Ta { get; } public double Vamax { get; }
    public double Vamin { get; } public double Vrmax { get; } public double Vrmin { get; } public double Kc { get; } public double Kf { get; }
    public double Tf { get; } public double Klr { get; } public double Ilr { get; }

    public Esst1a(double tr, double vimax, double vimin, double tc, double tb, double tc1, double tb1, double ka, double ta, double vamax, double vamin,
        double vrmax, double vrmin, double kc = 0, double kf = 0, double tf = 1, double klr = 0, double ilr = 0, int vos = 1)
    {
        Tr = tr; Vimax = vimax; Vimin = vimin; Tc = tc; Tb = tb; Tc1 = tc1; Tb1 = tb1; Ka = ka; Ta = ta; Vamax = vamax; Vamin = vamin;
        Vrmax = vrmax; Vrmin = vrmin; Kc = kc; Kf = kf; Tf = tf; Klr = klr; Ilr = ilr; Vos = vos;
        _vm = AddStateIf(Blocks.Active(tr), "Vm");
        _ll1 = new LeadLag(AddStateIf(Blocks.Active(tb), "LL1"), tc, tb);
        _ll2 = new LeadLag(AddStateIf(Blocks.Active(tb1), "LL2"), tc1, tb1);
        _va = AddStateIf(Blocks.Active(ta), "VA");
        _vf = AddStateIf(kf != 0 && Blocks.Active(tf), "Vf");
    }

    public static Esst1a FromDyr(ReadOnlySpan<double> c) => new(c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13], c[14],
        c[15], c[16], c[17], c[18], c[19], (int)c[1]);
    public override string ModelName => "ESST1A";

    private double OutputLimit(double va, MachineSignals s, double vs)
    {
        double v = va + (Vos == 2 ? vs : 0) - Klr * Math.Max(0, s.XadIfd - Ilr);
        return Blocks.Clamp(v, s.Vt * Vrmin, s.Vt * Vrmax - Kc * s.XadIfd);
    }

    public override void Initialize(double efd0, MachineSignals s, Span<double> x)
    {
        double va = efd0 + Klr * Math.Max(0, s.XadIfd - Ilr) - (Vos == 2 ? s.Vs : 0);
        double vi = va / Ka;
        if (_vm >= 0) x[_vm] = s.Vt;
        _ll1.Initialize(x, vi); _ll2.Initialize(x, vi);
        if (_va >= 0) x[_va] = va;
        if (_vf >= 0) x[_vf] = efd0;
        Vref = s.Vt + vi - (Vos == 1 ? s.Vs : 0);
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double vm = _vm >= 0 ? x[_vm] : s.Vt;
        if (_vm >= 0) dx[_vm] = (s.Vt - vm) / Tr;
        // rate feedback uses the previous Efd through its state (algebraic loop avoided)
        double fb = _vf >= 0 ? Kf / Tf * (LastEfd(x, s) - x[_vf]) : 0;
        double vi = Blocks.Clamp(Vref - vm + (Vos == 1 ? s.Vs : 0) - fb, Vimin, Vimax);
        _ll1.Derivative(x, vi, dx);
        double y1 = _ll1.Output(x, vi);
        _ll2.Derivative(x, y1, dx);
        double y2 = _ll2.Output(x, y1);
        double va;
        if (_va >= 0) { va = x[_va]; dx[_va] = Blocks.NonWindupLag(va, y2, Ka, Ta, Vamin, Vamax); }
        else va = Blocks.Clamp(Ka * y2, Vamin, Vamax);
        double efd = OutputLimit(va, s, s.Vs);
        if (_vf >= 0) dx[_vf] = (efd - x[_vf]) / Tf;
        return efd;
    }

    private double LastEfd(ReadOnlySpan<double> x, MachineSignals s) => _va >= 0 ? OutputLimit(x[_va], s, s.Vs) : s.Efd;

    public override void ApplyLimits(Span<double> x) => Clamp(x, _va, Vamin, Vamax);
}

/// <summary>
/// ESAC4A: IEEE 421.5 type AC4A (alternator-supplied controlled rectifier). Vi limited to [VIMAX, VIMIN], lead-lag TC/TB,
/// KA/(1+sTA) with non-windup limits [VRMIN, VRMAX − KC·Ifd]; Efd = VR. CONs: TR, VIMAX, VIMIN, TC, TB, KA, TA, VRMAX, VRMIN, KC.
/// </summary>
public sealed class Esac4a : ExciterModel
{
    public static readonly string[] ParameterNames = ["TR", "VIMAX", "VIMIN", "TC", "TB", "KA", "TA", "VRMAX", "VRMIN", "KC"];
    private readonly int _vm, _vr;
    private readonly LeadLag _ll;
    private double _ifd;

    public double Tr { get; } public double Vimax { get; } public double Vimin { get; } public double Tc { get; } public double Tb { get; }
    public double Ka { get; } public double Ta { get; } public double Vrmax { get; } public double Vrmin { get; } public double Kc { get; }

    public Esac4a(double tr, double vimax, double vimin, double tc, double tb, double ka, double ta, double vrmax, double vrmin, double kc)
    {
        Tr = tr; Vimax = vimax; Vimin = vimin; Tc = tc; Tb = tb; Ka = ka; Ta = ta; Vrmax = vrmax; Vrmin = vrmin; Kc = kc;
        _vm = AddStateIf(Blocks.Active(tr), "Vm");
        _ll = new LeadLag(AddStateIf(Blocks.Active(tb), "LL"), tc, tb);
        _vr = AddStateIf(Blocks.Active(ta), "VR");
    }

    public static Esac4a FromDyr(ReadOnlySpan<double> c) => new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9]);
    public override string ModelName => "ESAC4A";

    public override void Initialize(double efd0, MachineSignals s, Span<double> x)
    {
        double vi = efd0 / Ka;
        if (_vm >= 0) x[_vm] = s.Vt;
        _ll.Initialize(x, vi);
        if (_vr >= 0) x[_vr] = efd0;
        Vref = s.Vt + vi - s.Vs;
        _ifd = s.XadIfd;
    }

    public override double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx)
    {
        double vm = _vm >= 0 ? x[_vm] : s.Vt;
        if (_vm >= 0) dx[_vm] = (s.Vt - vm) / Tr;
        double vi = Blocks.Clamp(Vref - vm + s.Vs, Vimin, Vimax);
        _ll.Derivative(x, vi, dx);
        double y = _ll.Output(x, vi);
        _ifd = s.XadIfd;
        double max = Vrmax - Kc * _ifd;
        if (_vr < 0) return Blocks.Clamp(Ka * y, Vrmin, max);
        dx[_vr] = Blocks.NonWindupLag(x[_vr], y, Ka, Ta, Vrmin, max);
        return x[_vr];
    }

    public override void ApplyLimits(Span<double> x) => Clamp(x, _vr, Vrmin, Vrmax - Kc * _ifd);
}
