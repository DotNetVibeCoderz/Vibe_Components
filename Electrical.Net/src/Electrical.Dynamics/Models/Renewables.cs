using System.Numerics;

namespace Electrical.Dynamics;

/// <summary>
/// REGC_A: renewable generator/converter (WECC second generation). Current source in phase with the terminal voltage: active current Ip
/// and reactive current Iq (positive = capacitive) follow Ipcmd/Iqcmd through 1/(1+sTg). Ip recovery rate limited by RRPWR [pu/s],
/// Iq rate limited to [IQRMIN, IQRMAX] [pu/s]; low-voltage power logic (LVPL, measured through TFLTR) limits Ipcmd; low-voltage active
/// current management scales Ip between LVPNT0 and LVPNT1; high-voltage reactive current limiter reduces Iq by KHV·(V − VOLIM) above
/// VOLIM, not below IOLIM. ACCEL is ignored (no PLL in REGC_A). ICON: LVPLSW. CONs: TG, RRPWR, BRKPT, ZEROX, LVPL1, VOLIM, LVPNT1, LVPNT0,
/// IOLIM, TFLTR, KHV, IQRMAX, IQRMIN, ACCEL. Currents in pu on MBase.
/// </summary>
public sealed class Regca : ConverterModel
{
    public static readonly string[] ParameterNames = ["LVPLSW", "TG", "RRPWR", "BRKPT", "ZEROX", "LVPL1", "VOLIM", "LVPNT1", "LVPNT0", "IOLIM", "TFLTR", "KHV", "IQRMAX", "IQRMIN", "ACCEL"];
    private readonly int _ip, _iq, _vf;

    public bool LvplSwitch { get; init; } = true;
    public double Tg { get; } public double Rrpwr { get; } public double Brkpt { get; } public double Zerox { get; } public double Lvpl1 { get; }
    public double Volim { get; } public double Lvpnt1 { get; } public double Lvpnt0 { get; } public double Iolim { get; } public double Tfltr { get; }
    public double Khv { get; } public double Iqrmax { get; } public double Iqrmin { get; }

    public Regca(double tg = 0.02, double rrpwr = 10, double brkpt = 0.9, double zerox = 0.4, double lvpl1 = 1.22, double volim = 1.2,
        double lvpnt1 = 0.8, double lvpnt0 = 0.4, double iolim = -1.3, double tfltr = 0.02, double khv = 0.7, double iqrmax = 99, double iqrmin = -99)
    {
        Tg = tg; Rrpwr = rrpwr; Brkpt = brkpt; Zerox = zerox; Lvpl1 = lvpl1; Volim = volim; Lvpnt1 = lvpnt1; Lvpnt0 = lvpnt0; Iolim = iolim;
        Tfltr = tfltr; Khv = khv; Iqrmax = iqrmax; Iqrmin = iqrmin;
        if (!Blocks.Active(tg)) throw new ArgumentException("REGC_A requires TG > 0.");
        _ip = AddState("Ip"); _iq = AddState("Iq");
        _vf = AddStateIf(Blocks.Active(tfltr), "Vlvpl");
    }

    public static Regca FromDyr(ReadOnlySpan<double> c) =>
        new(c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13]) { LvplSwitch = c[0] != 0 };
    public override string ModelName => "REGCA1";

    /// <summary>LVPL characteristic [pu current] at filtered voltage <paramref name="v"/>.</summary>
    public double Lvpl(double v) => !LvplSwitch || v >= Brkpt ? double.PositiveInfinity : v <= Zerox ? 0 : Lvpl1 * (v - Zerox) / (Brkpt - Zerox);

    public override void Initialize(ConverterSignals s, Span<double> x)
    {
        double v = Math.Max(s.Vt, 1e-6);
        x[_ip] = s.P0 / v; x[_iq] = s.Q0 / v;
        if (_vf >= 0) x[_vf] = s.Vt;
        s.Ipcmd = x[_ip]; s.Iqcmd = x[_iq];
    }

    public override void Output(ReadOnlySpan<double> x, ConverterSignals s)
    {
        double v = s.Vt;
        double g = v >= Lvpnt1 ? 1 : v <= Lvpnt0 ? 0 : (v - Lvpnt0) / (Lvpnt1 - Lvpnt0);
        s.Ip = x[_ip] * g;
        double iq = x[_iq];
        if (v > Volim) iq = Math.Max(iq - Khv * (v - Volim), Math.Min(Iolim, iq));
        s.Iq = iq;
    }

    public override void Derivatives(ReadOnlySpan<double> x, ConverterSignals s, Span<double> dx)
    {
        double vf = _vf >= 0 ? x[_vf] : s.Vt;
        if (_vf >= 0) dx[_vf] = (s.Vt - vf) / Tfltr;
        double ipcmd = Math.Min(s.Ipcmd, Lvpl(vf));
        double dip = (ipcmd - x[_ip]) / Tg;
        if (dip > Rrpwr && x[_ip] >= 0) dip = Rrpwr;
        dx[_ip] = dip;
        dx[_iq] = Blocks.Clamp((s.Iqcmd - x[_iq]) / Tg, Iqrmin, Iqrmax);
    }
}

/// <summary>
/// REEC_A: renewable electrical control (WECC second generation), with documented simplifications:
/// voltage filter TRV; voltage-dip logic (V &lt; VDIP or V &gt; VUP) that freezes the PI integrators and Pord and injects
/// Iqinj = KQV·db(VREF0 − V) limited to [IQL1, IQH1], holding IQFRZ for THLD after the dip; reactive control QFLAG = 0 (Iq = Qcmd/V
/// through TIQ), QFLAG = 1 with VFLAG = 1 (Q PI → V PI) or VFLAG = 0 (Q PI output used directly as Iq); PFFLAG power-factor control;
/// active power Pord through TPORD with ramp [DPMIN, DPMAX] and limits [PMIN, PMAX]; current limit logic with voltage-dependent tables
/// VDL1/VDL2 and IMAX with Q priority (PQFLAG = 0) or P priority (PQFLAG = 1). PFLAG (speed multiplication) and THLD2 are ignored.
/// ICONs: remote bus, PFFLAG, VFLAG, QFLAG, PFLAG, PQFLAG. 45 CONs as in PSS/E (VDIP … IP4).
/// </summary>
public sealed class Reeca : ElectricalControlModel
{
    public static readonly string[] ParameterNames = ["IREG", "PFFLAG", "VFLAG", "QFLAG", "PFLAG", "PQFLAG", "VDIP", "VUP", "TRV", "DBD1", "DBD2", "KQV", "IQH1", "IQL1",
        "VREF0", "IQFRZ", "THLD", "THLD2", "TP", "QMAX", "QMIN", "VMAX", "VMIN", "KQP", "KQI", "KVP", "KVI", "VREF1", "TIQ", "DPMAX", "DPMIN", "PMAX", "PMIN",
        "IMAX", "TPORD", "VQ1", "IQ1", "VQ2", "IQ2", "VQ3", "IQ3", "VQ4", "IQ4", "VP1", "IP1", "VP2", "IP2", "VP3", "IP3", "VP4", "IP4"];

    private readonly int _vf, _pf, _qpi, _vpi, _iql, _pord;
    private readonly double[] _vq, _iqt, _vp, _ipt;
    private double _holdEnd = double.NegativeInfinity, _tanPf;
    private bool _wasDip, _lastDip;

    public bool PfFlag { get; init; }
    public bool VFlag { get; init; } = true;
    public bool QFlag { get; init; }
    public bool PqFlag { get; init; }
    public double Vdip { get; init; } = 0.9;
    public double Vup { get; init; } = 1.1;
    public double Trv { get; init; } = 0.02;
    public double Dbd1 { get; init; } = -0.05;
    public double Dbd2 { get; init; } = 0.05;
    public double Kqv { get; init; } = 2;
    public double Iqh1 { get; init; } = 1.05;
    public double Iql1 { get; init; } = -1.05;
    /// <summary>Voltage reference for the dip injection [pu] (0 = initial voltage).</summary>
    public double Vref0 { get; set; }
    public double Iqfrz { get; init; }
    public double Thld { get; init; }
    public double Tp { get; init; } = 0.05;
    public double Qmax { get; init; } = 0.4;
    public double Qmin { get; init; } = -0.4;
    public double Vmax { get; init; } = 1.1;
    public double Vmin { get; init; } = 0.9;
    public double Kqp { get; init; }
    public double Kqi { get; init; } = 0.1;
    public double Kvp { get; init; }
    public double Kvi { get; init; } = 40;
    public double Tiq { get; init; } = 0.02;
    public double Dpmax { get; init; } = 99;
    public double Dpmin { get; init; } = -99;
    public double Pmax { get; init; } = 1;
    public double Pmin { get; init; }
    public double Imax { get; init; } = 1.1;
    public double Tpord { get; init; } = 0.02;

    /// <summary>Power reference [pu on MBase] used when no plant controller is attached.</summary>
    public double Pref { get; set; }
    /// <summary>Reactive power reference [pu on MBase] used when no plant controller is attached.</summary>
    public double Qref { get; set; }

    /// <param name="vdl1">VDL1 table (V, Iq max) pairs; null = Imax.</param>
    /// <param name="vdl2">VDL2 table (V, Ip max) pairs; null = Imax.</param>
    public Reeca(bool qFlag = false, bool vFlag = true, bool pqFlag = false, double trv = 0.02, double tp = 0.05, double tiq = 0.02, double tpord = 0.02,
        (double V, double I)[]? vdl1 = null, (double V, double I)[]? vdl2 = null)
    {
        QFlag = qFlag; VFlag = vFlag; PqFlag = pqFlag; Trv = trv; Tp = tp; Tiq = tiq; Tpord = tpord;
        static (double[], double[]) Table((double V, double I)[]? t)
        {
            var pts = (t ?? []).Where(p => p.V != 0 || p.I != 0).OrderBy(p => p.V).ToArray();
            return (pts.Select(p => p.V).ToArray(), pts.Select(p => p.I).ToArray());
        }
        (_vq, _iqt) = Table(vdl1);
        (_vp, _ipt) = Table(vdl2);
        _vf = AddStateIf(Blocks.Active(trv), "Vf");
        _pf = AddStateIf(Blocks.Active(tp), "Pf");
        _qpi = AddStateIf(qFlag, "Qpi");
        _vpi = AddStateIf(qFlag && vFlag, "Vpi");
        _iql = AddStateIf(!qFlag && Blocks.Active(tiq), "Iqlag");
        _pord = AddStateIf(Blocks.Active(tpord), "Pord");
    }

    public static Reeca FromDyr(ReadOnlySpan<double> c)
    {
        var k = c[6..].ToArray();
        (double, double)[] T(int o) => [(k[o], k[o + 1]), (k[o + 2], k[o + 3]), (k[o + 4], k[o + 5]), (k[o + 6], k[o + 7])];
        return new Reeca(c[3] != 0, c[2] != 0, c[5] != 0, k[2], k[12], k[22], k[28], T(29), T(37))
        {
            PfFlag = c[1] != 0, Vdip = k[0], Vup = k[1], Dbd1 = k[3], Dbd2 = k[4], Kqv = k[5], Iqh1 = k[6], Iql1 = k[7], Vref0 = k[8], Iqfrz = k[9],
            Thld = k[10], Qmax = k[13], Qmin = k[14], Vmax = k[15], Vmin = k[16], Kqp = k[17], Kqi = k[18], Kvp = k[19], Kvi = k[20],
            Dpmax = k[23], Dpmin = k[24], Pmax = k[25], Pmin = k[26], Imax = k[27],
        };
    }

    public override string ModelName => "REECA1";

    private double Vdl(double[] vs, double[] ii, double v) => vs.Length == 0 ? Imax : Blocks.Interpolate(vs, ii, v);

    public override void ResetDiscrete() { _holdEnd = double.NegativeInfinity; _wasDip = false; _lastDip = false; }

    public override void Initialize(ConverterSignals s, Span<double> x)
    {
        if (Vref0 <= 0) Vref0 = s.Vt;
        if (_vf >= 0) x[_vf] = s.Vt;
        if (_pf >= 0) x[_pf] = s.P0;
        _tanPf = Math.Abs(s.P0) > 1e-9 ? s.Q0 / s.P0 : 0;
        Pref = s.P0; Qref = s.Q0;
        double iq0 = s.Iqcmd;
        if (_qpi >= 0) x[_qpi] = VFlag ? s.Vt : iq0;  // Q PI output = voltage reference (VFLAG = 1) or Iq (VFLAG = 0)
        if (_vpi >= 0) x[_vpi] = iq0;
        if (_iql >= 0) x[_iql] = iq0;
        if (_pord >= 0) x[_pord] = s.P0;
        ResetDiscrete();
    }

    public override bool TrySetReference(string name, double value)
    {
        if (name.Equals("Pref", StringComparison.OrdinalIgnoreCase)) { Pref = value; return true; }
        if (name.Equals("Qref", StringComparison.OrdinalIgnoreCase)) { Qref = value; return true; }
        return false;
    }

    public override bool TryGetReference(string name, out double value)
    {
        value = name.Equals("Pref", StringComparison.OrdinalIgnoreCase) ? Pref : name.Equals("Qref", StringComparison.OrdinalIgnoreCase) ? Qref : double.NaN;
        return !double.IsNaN(value);
    }

    public override void Evaluate(double t, ReadOnlySpan<double> x, ConverterSignals s, Span<double> dx)
    {
        double vf = _vf >= 0 ? x[_vf] : s.Vt;
        if (_vf >= 0) dx[_vf] = (s.Vt - vf) / Trv;
        double pf = _pf >= 0 ? x[_pf] : s.P;
        if (_pf >= 0) dx[_pf] = (s.P - pf) / Tp;
        bool dip = vf < Vdip || vf > Vup;
        _lastDip = dip;
        s.VoltageDip = dip;
        double vg = Math.Max(vf, 0.01);

        double iqinj = dip ? Blocks.Clamp(Kqv * Blocks.DeadBand(Vref0 - vf, Dbd1, Dbd2), Iql1, Iqh1) : t < _holdEnd ? Iqfrz : 0;
        double qcmd = Blocks.Clamp(PfFlag ? pf * _tanPf : s.Qext, Qmin, Qmax);
        double iq1;
        if (!QFlag)
        {
            double target = qcmd / vg;
            if (_iql >= 0) { iq1 = x[_iql]; dx[_iql] = dip ? 0 : (target - iq1) / Tiq; }
            else iq1 = target;
        }
        else
        {
            double eq = qcmd - s.Q;
            double lo = VFlag ? Vmin : -Imax, hi = VFlag ? Vmax : Imax;
            double qout = Blocks.Clamp(Kqp * eq + x[_qpi], lo, hi);
            dx[_qpi] = dip ? 0 : Blocks.NonWindupIntegrator(x[_qpi], Kqi * eq, lo, hi);
            if (VFlag)
            {
                double ev = qout - vf;
                iq1 = Kvp * ev + x[_vpi];
                dx[_vpi] = dip ? 0 : Blocks.NonWindupIntegrator(x[_vpi], Kvi * ev, -Imax, Imax);
            }
            else iq1 = qout;
        }

        double pin = s.Pext;
        double pord;
        if (_pord >= 0)
        {
            pord = x[_pord];
            dx[_pord] = dip ? 0 : Blocks.NonWindupIntegrator(pord, Blocks.Clamp((pin - pord) / Tpord, Dpmin, Dpmax), Pmin, Pmax);
        }
        else pord = Blocks.Clamp(pin, Pmin, Pmax);

        double vdl1 = Vdl(_vq, _iqt, vf), vdl2 = Vdl(_vp, _ipt, vf);
        double ipraw = pord / vg, iqraw = iq1 + iqinj;
        if (!PqFlag)
        {
            double iqmax = Math.Min(vdl1, Imax);
            s.Iqcmd = Blocks.Clamp(iqraw, -iqmax, iqmax);
            double ipmax = Math.Min(vdl2, Math.Sqrt(Math.Max(0, Imax * Imax - s.Iqcmd * s.Iqcmd)));
            s.Ipcmd = Blocks.Clamp(ipraw, 0, ipmax);
        }
        else
        {
            double ipmax = Math.Min(vdl2, Imax);
            s.Ipcmd = Blocks.Clamp(ipraw, 0, ipmax);
            double iqmax = Math.Min(vdl1, Math.Sqrt(Math.Max(0, Imax * Imax - s.Ipcmd * s.Ipcmd)));
            s.Iqcmd = Blocks.Clamp(iqraw, -iqmax, iqmax);
        }
    }

    public override void AfterStep(double t, ReadOnlySpan<double> x)
    {
        bool dip = _vf >= 0 ? x[_vf] < Vdip || x[_vf] > Vup : _lastDip;
        if (_wasDip && !dip && Thld > 0) _holdEnd = t + Thld;
        _wasDip = dip;
    }

    public override void ApplyLimits(Span<double> x)
    {
        Clamp(x, _pord, Pmin, Pmax);
        if (_qpi >= 0) x[_qpi] = VFlag ? Blocks.Clamp(x[_qpi], Vmin, Vmax) : Blocks.Clamp(x[_qpi], -Imax, Imax);
        Clamp(x, _vpi, -Imax, Imax);
    }
}

/// <summary>
/// REPC_A: renewable plant controller (WECC second generation), measured at the unit terminal (the regulated bus and branch ICONs are
/// read but the measurement is local — documented simplification). Voltage control (REFFLAG = 1) with droop KC·Q (VCFLAG = 0) or
/// line-drop compensation |V − (RC + jXC)·I| (VCFLAG = 1), or Q control (REFFLAG = 0); filters TFLTR; dead band DBD; error limits
/// [EMIN, EMAX]; PI (KP, KI) frozen below VFRZ, limited to [QMIN, QMAX]; lead-lag TFT/TFV → Qext. Frequency droop (FFLAG = 1) with dead
/// band [FDBD1, FDBD2], gains DDN (over-frequency) / DUP (under-frequency), P filter TP, error limits [FEMIN, FEMAX], PI (KPG, KIG), limits
/// [PMIN, PMAX], lag TG → Pext. Frequency is measured with a 20 ms washout of the terminal-voltage angle.
/// ICONs: IREG, FROM, TO, CKT, VCFLAG, REFFLAG, FFLAG. CONs: TFLTR, KP, KI, TFT, TFV, VFRZ, RC, XC, KC, EMAX, EMIN, DBD, QMAX, QMIN, KPG, KIG,
/// TP, FDBD1, FDBD2, FEMAX, FEMIN, PMAX, PMIN, TG, DDN, DUP. The default KP/KI (18/5) are voltage-control gains; Q control
/// (REFFLAG = 0) needs much lower gains (e.g. 0.5/2), otherwise the Q loop limit-cycles.
/// </summary>
public sealed class Repca : PlantControlModel
{
    public static readonly string[] ParameterNames = ["IREG", "FROM", "TO", "CKT", "VCFLAG", "REFFLAG", "FFLAG", "TFLTR", "KP", "KI", "TFT", "TFV", "VFRZ", "RC", "XC", "KC",
        "EMAX", "EMIN", "DBD", "QMAX", "QMIN", "KPG", "KIG", "TP", "FDBD1", "FDBD2", "FEMAX", "FEMIN", "PMAX", "PMIN", "TG", "DDN", "DUP"];
    private const double FrequencyFilter = 0.02;
    private readonly int _vm, _qm, _qi, _ll, _pm, _pi, _pg, _fz;

    public bool VcFlag { get; init; }
    public bool RefFlag { get; init; } = true;
    public bool FFlag { get; }
    public double Tfltr { get; } = 0.02;
    public double Kp { get; init; } = 18;
    public double Ki { get; init; } = 5;
    public double Tft { get; } public double Tfv { get; } = 0.075;
    public double Vfrz { get; init; }
    public double Rc { get; init; } public double Xc { get; init; } public double Kc { get; init; }
    public double Emax { get; init; } = 0.1; public double Emin { get; init; } = -0.1;
    public double Dbd { get; init; }
    public double Qmax { get; init; } = 0.4; public double Qmin { get; init; } = -0.4;
    public double Kpg { get; init; } = 0.1; public double Kig { get; init; } = 0.05;
    public double Tp { get; } = 0.25;
    public double Fdbd1 { get; init; } = -0.0006; public double Fdbd2 { get; init; } = 0.0006;
    public double Femax { get; init; } = 999; public double Femin { get; init; } = -999;
    public double Pmax { get; init; } = 1; public double Pmin { get; init; }
    public double Tg { get; } = 0.1;
    public double Ddn { get; init; } = 20; public double Dup { get; init; } = 20;
    /// <summary>Synchronous angular frequency [rad/s] (set by the system builder).</summary>
    public double OmegaS { get; set; } = 2 * Math.PI * 50;

    public double Vref { get; set; }
    public double Qref { get; set; }
    public double Pref { get; set; }

    public Repca(bool fFlag = true, double tfltr = 0.02, double tft = 0, double tfv = 0.075, double tp = 0.25, double tg = 0.1)
    {
        FFlag = fFlag; Tfltr = tfltr; Tft = tft; Tfv = tfv; Tp = tp; Tg = tg;
        _vm = AddStateIf(Blocks.Active(tfltr), "Vmeas");
        _qm = AddStateIf(Blocks.Active(tfltr), "Qmeas");
        _qi = AddState("Qpi");
        _ll = AddStateIf(Blocks.Active(tfv), "Qll");
        _pm = AddStateIf(fFlag && Blocks.Active(tp), "Pmeas");
        _pi = AddStateIf(fFlag, "Ppi");
        _pg = AddStateIf(fFlag && Blocks.Active(tg), "Pg");
        _fz = AddStateIf(fFlag, "fmeas");
    }

    public static Repca FromDyr(ReadOnlySpan<double> c)
    {
        var k = c[7..].ToArray();
        return new Repca(c[6] != 0, k[0], k[3], k[4], k[16], k[23])
        {
            VcFlag = c[4] != 0, RefFlag = c[5] != 0, Kp = k[1], Ki = k[2], Vfrz = k[5], Rc = k[6], Xc = k[7], Kc = k[8], Emax = k[9], Emin = k[10],
            Dbd = k[11], Qmax = k[12], Qmin = k[13], Kpg = k[14], Kig = k[15], Fdbd1 = k[17], Fdbd2 = k[18], Femax = k[19], Femin = k[20],
            Pmax = k[21], Pmin = k[22], Ddn = k[24], Dup = k[25],
        };
    }

    public override string ModelName => "REPCA1";

    private double CompensatedVoltage(ConverterSignals s)
    {
        if (VcFlag)
        {
            var i = new Complex(s.Ip, -s.Iq) * Complex.FromPolarCoordinates(1, s.V.Phase);
            return (s.V - new Complex(Rc, Xc) * i).Magnitude;
        }
        return s.Vt + Kc * s.Q;
    }

    public override void Initialize(ConverterSignals s, Span<double> x)
    {
        double vc = CompensatedVoltage(s);
        Vref = vc; Qref = s.Q0; Pref = s.P0;
        if (_vm >= 0) x[_vm] = vc;
        if (_qm >= 0) x[_qm] = s.Q0;
        x[_qi] = s.Q0;
        if (_ll >= 0) x[_ll] = s.Q0;
        if (_pm >= 0) x[_pm] = s.P0;
        if (_pi >= 0) x[_pi] = s.P0;
        if (_pg >= 0) x[_pg] = s.P0;
        if (_fz >= 0) x[_fz] = s.V.Phase;
        s.Pext = s.P0; s.Qext = s.Q0;
    }

    public override bool TrySetReference(string name, double value)
    {
        if (name.Equals("Vref", StringComparison.OrdinalIgnoreCase)) Vref = value;
        else if (name.Equals("Qref", StringComparison.OrdinalIgnoreCase)) Qref = value;
        else if (name.Equals("Pref", StringComparison.OrdinalIgnoreCase)) Pref = value;
        else return false;
        return true;
    }

    public override bool TryGetReference(string name, out double value)
    {
        value = name.ToUpperInvariant() switch { "VREF" => Vref, "QREF" => Qref, "PREF" => Pref, _ => double.NaN };
        return !double.IsNaN(value);
    }

    public override void Evaluate(ReadOnlySpan<double> x, ConverterSignals s, Span<double> dx)
    {
        double vc = CompensatedVoltage(s);
        double vm = _vm >= 0 ? x[_vm] : vc;
        if (_vm >= 0) dx[_vm] = (vc - vm) / Tfltr;
        double qm = _qm >= 0 ? x[_qm] : s.Q;
        if (_qm >= 0) dx[_qm] = (s.Q - qm) / Tfltr;
        double err = RefFlag ? Vref - vm : Qref - qm;
        err = Blocks.Clamp(Blocks.DeadBand(err, -Dbd, Dbd), Emin, Emax);
        bool freeze = s.Vt < Vfrz;
        double q = Blocks.Clamp(Kp * err + x[_qi], Qmin, Qmax);
        dx[_qi] = freeze ? 0 : Blocks.NonWindupIntegrator(x[_qi], Ki * err, Qmin, Qmax);
        if (_ll >= 0) { dx[_ll] = (q - x[_ll]) / Tfv; s.Qext = Blocks.LeadLagOutput(x[_ll], q, Tft, Tfv); }
        else s.Qext = q;

        if (!FFlag) { s.Pext = Pref; s.FrequencyDeviation = 0; return; }
        double dth = Blocks.WrapAngle(s.V.Phase - x[_fz]);
        dx[_fz] = dth / FrequencyFilter;
        double fdev = dth / (FrequencyFilter * OmegaS);
        s.FrequencyDeviation = fdev;
        double ferr = Blocks.DeadBand(-fdev, Fdbd1, Fdbd2);
        double pdroop = ferr > 0 ? Dup * ferr : Ddn * ferr;
        double pm = _pm >= 0 ? x[_pm] : s.P;
        if (_pm >= 0) dx[_pm] = (s.P - pm) / Tp;
        double perr = Blocks.Clamp(Pref + pdroop - pm, Femin, Femax);
        double p = Blocks.Clamp(Kpg * perr + x[_pi], Pmin, Pmax);
        dx[_pi] = Blocks.NonWindupIntegrator(x[_pi], Kig * perr, Pmin, Pmax);
        if (_pg >= 0) { dx[_pg] = (p - x[_pg]) / Tg; s.Pext = x[_pg]; }
        else s.Pext = p;
    }

    public override void ApplyLimits(Span<double> x)
    {
        Clamp(x, _qi, Qmin, Qmax);
        Clamp(x, _pi, Pmin, Pmax);
    }
}

/// <summary>Control law of a <see cref="GridFormingInverter"/>.</summary>
public enum GridFormingMode
{
    /// <summary>P–f and Q–V droop with first-order power measurement filters.</summary>
    Droop,
    /// <summary>Virtual synchronous machine: 2H·dΔω/dt = Pset − P − D·Δω, Q–V droop.</summary>
    VirtualSynchronousMachine,
}

/// <summary>
/// Simple grid-forming inverter: internal voltage E∠θ behind a coupling reactance XF (pu on MBase). Droop mode:
/// ω = 1 + MP·(Pset − Pf), E = Eset + MQ·(Qset − Qf) with filters TP; VSM mode: inertia H and damping D (default 1/MP).
/// Inner current limit: when |(E − V)/(jXF)| exceeds IMAX the output current keeps its direction and is scaled to IMAX
/// (simplification of a current-reference saturation). Not a WECC REGFM model; the .dyr names GFMDRP/GFMVSM are library-specific.
/// </summary>
public sealed class GridFormingInverter : DynamicComponent
{
    public static readonly string[] ParameterNames = ["XF", "MP", "MQ", "TP", "IMAX", "H", "D"];
    private readonly int _th, _dw, _pf, _qf;

    public GridFormingInverter(GridFormingMode mode = GridFormingMode.Droop, double xf = 0.15, double mp = 0.05, double mq = 0.05, double tp = 0.02,
        double imax = 1.2, double h = 2, double d = 0)
    {
        Mode = mode; Xf = xf; Mp = mp; Mq = mq; Tp = tp; Imax = imax; H = h; D = d > 0 ? d : 1 / mp;
        _th = AddState("theta");
        _dw = AddStateIf(mode == GridFormingMode.VirtualSynchronousMachine, "dw");
        _pf = AddStateIf(mode == GridFormingMode.Droop, "Pf");
        _qf = AddState("Qf");
    }

    public static GridFormingInverter FromDyr(GridFormingMode mode, ReadOnlySpan<double> c) =>
        new(mode, c[0], c[1], c[2], c[3], c[4], c.Length > 5 ? c[5] : 2, c.Length > 6 ? c[6] : 0);

    public override string ModelName => Mode == GridFormingMode.Droop ? "GFMDRP" : "GFMVSM";
    public override ModelCategory Category => ModelCategory.GridForming;
    public GridFormingMode Mode { get; }
    public double Xf { get; } public double Mp { get; } public double Mq { get; } public double Tp { get; } public double Imax { get; }
    public double H { get; } public double D { get; }
    /// <summary>Converter MVA base (0 = take <c>Generator.MBase</c>).</summary>
    public double MBase { get; set; }
    public double OmegaS { get; set; } = 2 * Math.PI * 50;
    public double Pset { get; set; }
    public double Qset { get; set; }
    public double Eset { get; set; }

    public int AngleState => _th;

    /// <summary>Output current (machine base) for terminal voltage <paramref name="v"/>; <paramref name="limited"/> is true at the current limit.</summary>
    public Complex Current(ReadOnlySpan<double> x, Complex v, out bool limited)
    {
        double e = Eset + Mq * (Qset - x[_qf]);
        var i = (Complex.FromPolarCoordinates(e, x[_th]) - v) / new Complex(0, Xf);
        limited = i.Magnitude > Imax;
        return limited ? i * (Imax / i.Magnitude) : i;
    }

    public double Speed(ReadOnlySpan<double> x) => 1 + (Mode == GridFormingMode.Droop ? Mp * (Pset - x[_pf]) : x[_dw]);

    public void Initialize(Complex v, Complex s, Span<double> x)
    {
        var i = Complex.Conjugate(s / v);
        var e = v + new Complex(0, Xf) * i;
        x[_th] = e.Phase;
        if (_dw >= 0) x[_dw] = 0;
        if (_pf >= 0) x[_pf] = s.Real;
        x[_qf] = s.Imaginary;
        Pset = s.Real; Qset = s.Imaginary; Eset = e.Magnitude;
    }

    public void Derivatives(ReadOnlySpan<double> x, double p, double q, Span<double> dx)
    {
        if (Mode == GridFormingMode.Droop)
        {
            dx[_th] = OmegaS * Mp * (Pset - x[_pf]);
            dx[_pf] = (p - x[_pf]) / Tp;
        }
        else
        {
            dx[_th] = OmegaS * x[_dw];
            dx[_dw] = (Pset - p - D * x[_dw]) / (2 * H);
        }
        dx[_qf] = (q - x[_qf]) / Tp;
    }

    public override bool TrySetReference(string name, double value)
    {
        switch (name.ToUpperInvariant())
        {
            case "PREF": Pset = value; return true;
            case "QREF": Qset = value; return true;
            case "VREF": Eset = value; return true;
            default: return false;
        }
    }

    public override bool TryGetReference(string name, out double value)
    {
        value = name.ToUpperInvariant() switch { "PREF" => Pset, "QREF" => Qset, "VREF" => Eset, _ => double.NaN };
        return !double.IsNaN(value);
    }
}
