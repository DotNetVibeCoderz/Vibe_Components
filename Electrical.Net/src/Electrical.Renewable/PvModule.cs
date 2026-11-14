namespace Electrical.Renewable;

/// <summary>PV module datasheet values at STC (1000 W/m², 25 °C).</summary>
/// <param name="Voc">Open-circuit voltage [V].</param>
/// <param name="Isc">Short-circuit current [A].</param>
/// <param name="Vmp">Maximum-power voltage [V].</param>
/// <param name="Imp">Maximum-power current [A].</param>
/// <param name="CellsInSeries">Number of series cells Ns.</param>
/// <param name="KiAPerK">Temperature coefficient of Isc [A/K].</param>
/// <param name="KvVPerK">Temperature coefficient of Voc [V/K] (negative).</param>
public sealed record PvDatasheet(double Voc, double Isc, double Vmp, double Imp, int CellsInSeries, double KiAPerK, double KvVPerK)
{
    public double Pmax => Vmp * Imp;

    /// <summary>Kyocera KC200GT (200 W multicrystalline), the reference module of Villalva et al. (2009).</summary>
    public static PvDatasheet KyoceraKC200GT { get; } = new(32.9, 8.21, 26.3, 7.61, 54, 0.0032, -0.1230);
}

/// <summary>Point on an I–V curve.</summary>
public readonly record struct IvPoint(double Voltage, double Current)
{
    public double Power => Voltage * Current;
}

/// <summary>
/// Five-parameter single-diode PV model I = Ipv − I0·[exp((V + I·Rs)/(a·Vt)) − 1] − (V + I·Rs)/Rp with Vt = Ns·k·T/q.
/// Parameters are extracted from the datasheet with the Villalva, Gazoli &amp; Filho (2009) method: the ideality factor a is chosen,
/// I0 follows from Voc, and Rs is found so that the model's maximum power point coincides with (Vmp, Imp) while Rp is solved from
/// the (Vmp, Imp) condition. Temperature/irradiance translation follows the same paper (Ipv ∝ G with Ki, I0 from Voc + Kv·ΔT).
/// For the KC200GT the paper reports Rs = 0.221 Ω, Rp = 415.405 Ω, Ipv = 8.214 A, I0 = 9.825·10⁻⁸ A (a = 1.3).
/// The implicit equation is solved per point by a monotone Newton iteration (convergent because I ↦ f(I) is concave and decreasing).
/// </summary>
public sealed class SingleDiodeModel
{
    private const double K = 1.380649e-23, Q = 1.602176634e-19, Tstc = 298.15, Gstc = 1000;

    private SingleDiodeModel(PvDatasheet ds, double a, double rs, double rp, double ipv, double i0)
    {
        Datasheet = ds; IdealityFactor = a; Rs = rs; Rp = rp; IpvStc = ipv; I0Stc = i0;
    }

    public PvDatasheet Datasheet { get; }
    public double IdealityFactor { get; }
    /// <summary>Series resistance [Ω].</summary>
    public double Rs { get; }
    /// <summary>Parallel (shunt) resistance [Ω].</summary>
    public double Rp { get; }
    /// <summary>Photo-generated current at STC [A].</summary>
    public double IpvStc { get; }
    /// <summary>Diode saturation current at STC [A].</summary>
    public double I0Stc { get; }

    /// <summary>Constructs a model from explicit parameters (STC values).</summary>
    public static SingleDiodeModel FromParameters(PvDatasheet ds, double a, double rs, double rp, double ipv, double i0) => new(ds, a, rs, rp, ipv, i0);

    /// <summary>Extracts the five parameters from a datasheet (Villalva method).</summary>
    /// <param name="idealityFactor">Diode ideality factor a (1.0–1.5).</param>
    /// <param name="pmaxToleranceW">
    /// Villalva's stopping rule: Rs is incremented in 1 mΩ steps until the model peak exceeds the datasheet Pmax by at most this amount [W]
    /// (0.006 W reproduces the paper's KC200GT values). Use 0 for the exact solution where the model MPP coincides with (Vmp, Imp).
    /// </param>
    public static SingleDiodeModel FromDatasheet(PvDatasheet ds, double idealityFactor = 1.3, double pmaxToleranceW = 0.006)
    {
        double a = idealityFactor;
        double vt = ds.CellsInSeries * K * Tstc / Q;
        double i0 = ds.Isc / (Math.Exp(ds.Voc / (a * vt)) - 1);
        double vmp = ds.Vmp, imp = ds.Imp, pmax = ds.Pmax;

        // For a given Rs: solve Rp (and Ipv) such that the curve passes through (Vmp, Imp); return dP/dV at Vmp.
        (double Rp, double Ipv, double Slope) Eval(double rs)
        {
            double ipv = ds.Isc, rp = double.NaN;
            for (int it = 0; it < 200; it++)
            {
                double den = vmp * ipv - vmp * i0 * Math.Exp((vmp + imp * rs) / (a * vt)) + vmp * i0 - pmax;
                if (den <= 0) return (double.NaN, ipv, double.NaN);
                double rpNew = vmp * (vmp + imp * rs) / den;
                double ipvNew = (rpNew + rs) / rpNew * ds.Isc;
                bool done = Math.Abs(rpNew - rp) < 1e-9 * Math.Abs(rpNew);
                rp = rpNew; ipv = ipvNew;
                if (done) break;
            }
            double ex = i0 / (a * vt) * Math.Exp((vmp + imp * rs) / (a * vt));
            double dfdv = -ex - 1 / rp, dfdi = -ex * rs - rs / rp - 1;
            double didv = -dfdv / dfdi;
            return (rp, ipv, imp + vmp * didv);
        }

        // Scan Rs for the sign change of dP/dV at Vmp, then bisect.
        double lo = 0, hi = double.NaN;
        var e0 = Eval(0);
        if (double.IsNaN(e0.Slope) || e0.Slope < 0) throw new InvalidOperationException("Datasheet values are inconsistent with the single-diode model.");
        for (double rs = 0.001; rs < 5; rs += 0.001)
        {
            var e = Eval(rs);
            if (double.IsNaN(e.Slope) || e.Slope <= 0) { hi = rs; break; }
            lo = rs;
        }
        if (double.IsNaN(hi)) throw new InvalidOperationException("Could not bracket the series resistance.");
        if (pmaxToleranceW > 0)
        {
            // Villalva's procedure: increment Rs (1 mΩ) until the model peak power is within the tolerance of the datasheet Pmax.
            for (double rs = 0; rs <= hi; rs += 0.001)
            {
                var e = Eval(rs);
                if (double.IsNaN(e.Rp)) break;
                var m = new SingleDiodeModel(ds, a, rs, e.Rp, e.Ipv, i0);
                if (m.MaximumPowerPoint().Power - pmax <= pmaxToleranceW) return m;
            }
        }
        for (int it = 0; it < 60; it++)
        {
            double mid = 0.5 * (lo + hi);
            var e = Eval(mid);
            if (!double.IsNaN(e.Slope) && e.Slope > 0) lo = mid; else hi = mid;
        }
        var best = Eval(lo);
        return new SingleDiodeModel(ds, a, lo, best.Rp, best.Ipv, i0);
    }

    /// <summary>Thermal voltage Ns·k·T/q [V] at cell temperature <paramref name="cellTempC"/>.</summary>
    public double ThermalVoltage(double cellTempC) => Datasheet.CellsInSeries * K * (cellTempC + 273.15) / Q;

    /// <summary>Photo current [A] at irradiance [W/m²] and cell temperature [°C].</summary>
    public double PhotoCurrent(double irradiance, double cellTempC) =>
        (IpvStc + Datasheet.KiAPerK * (cellTempC - 25)) * irradiance / Gstc;

    /// <summary>Saturation current [A] at cell temperature [°C] (Villalva eq. 7).</summary>
    public double SaturationCurrent(double cellTempC)
    {
        double dt = cellTempC - 25;
        double vt = ThermalVoltage(cellTempC);
        return (Datasheet.Isc + Datasheet.KiAPerK * dt) / (Math.Exp((Datasheet.Voc + Datasheet.KvVPerK * dt) / (IdealityFactor * vt)) - 1);
    }

    /// <summary>Module current [A] at terminal voltage [V], irradiance [W/m²] and cell temperature [°C].</summary>
    public double Current(double voltage, double irradiance = 1000, double cellTempC = 25)
    {
        double ipv = PhotoCurrent(irradiance, cellTempC), i0 = SaturationCurrent(cellTempC);
        double avt = IdealityFactor * ThermalVoltage(cellTempC);
        double i = ipv + i0 + Math.Max(0, -voltage / Rp) + 1e-6; // f(i) <= 0 here: monotone Newton from the right
        for (int it = 0; it < 100; it++)
        {
            double ex = Math.Exp(Math.Min((voltage + i * Rs) / avt, 700));
            double f = ipv - i0 * (ex - 1) - (voltage + i * Rs) / Rp - i;
            double df = -i0 * Rs / avt * ex - Rs / Rp - 1;
            double step = f / df;
            i -= step;
            if (Math.Abs(step) < 1e-12 * Math.Max(1, Math.Abs(i))) break;
        }
        return i;
    }

    /// <summary>Open-circuit voltage [V] (bisection on I(V) = 0).</summary>
    public double OpenCircuitVoltage(double irradiance = 1000, double cellTempC = 25)
    {
        if (irradiance <= 0) return 0;
        double lo = 0, hi = Datasheet.Voc * 1.5 + 5;
        for (int it = 0; it < 80; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (Current(mid, irradiance, cellTempC) > 0) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>I–V curve from 0 to Voc with <paramref name="points"/> samples.</summary>
    public IvPoint[] IvCurve(double irradiance = 1000, double cellTempC = 25, int points = 100)
    {
        double voc = OpenCircuitVoltage(irradiance, cellTempC);
        var r = new IvPoint[points];
        for (int k = 0; k < points; k++)
        {
            double v = voc * k / (points - 1);
            r[k] = new IvPoint(v, Math.Max(0, Current(v, irradiance, cellTempC)));
        }
        return r;
    }

    /// <summary>Maximum power point (golden-section search on P(V)).</summary>
    public IvPoint MaximumPowerPoint(double irradiance = 1000, double cellTempC = 25)
    {
        double voc = OpenCircuitVoltage(irradiance, cellTempC);
        if (voc <= 0) return default;
        double a = 0, b = voc, g = (Math.Sqrt(5) - 1) / 2;
        double c = b - g * (b - a), d = a + g * (b - a);
        double P(double v) => v * Current(v, irradiance, cellTempC);
        double fc = P(c), fd = P(d);
        for (int it = 0; it < 100 && b - a > 1e-7; it++)
        {
            if (fc > fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = P(c); }
            else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = P(d); }
        }
        double vm = 0.5 * (a + b);
        return new IvPoint(vm, Current(vm, irradiance, cellTempC));
    }
}

/// <summary>Simplified PVWatts (v5, Dobos 2014) DC and inverter models.</summary>
public static class PvWatts
{
    /// <summary>DC power [W] = Pdc0 · G_poa/1000 · (1 + γ(Tc − 25)).</summary>
    /// <param name="pdc0">DC nameplate at STC [W] (any power unit; output uses the same unit).</param>
    /// <param name="gammaPerC">Power temperature coefficient [1/°C], default −0.0047 (standard module).</param>
    public static double DcPower(double poaWm2, double cellTempC, double pdc0, double gammaPerC = -0.0047) =>
        poaWm2 <= 0 ? 0 : pdc0 * poaWm2 / 1000 * (1 + gammaPerC * (cellTempC - 25));

    /// <summary>
    /// PVWatts inverter: η = η_nom/η_ref·(−0.0162ζ − 0.0059/ζ + 0.9858), ζ = Pdc/Pdc0 with Pdc0 = Pac0/η_nom, η_ref = 0.9637;
    /// output clipped at <paramref name="pac0"/>.
    /// </summary>
    public static double InverterAc(double pdc, double pac0, double nominalEfficiency = 0.96)
    {
        if (pdc <= 0) return 0;
        double pdc0 = pac0 / nominalEfficiency;
        double z = pdc / pdc0;
        double eta = nominalEfficiency / 0.9637 * (-0.0162 * z - 0.0059 / z + 0.9858);
        return Math.Min(Math.Max(0, eta * pdc), pac0);
    }

    /// <summary>
    /// End-to-end PVWatts estimate [W]: POA (isotropic) → NOCT cell temperature → DC with <paramref name="systemLosses"/> → inverter.
    /// </summary>
    public static double AcPower(IrradianceComponents irr, SunPosition sun, double tiltDeg, double surfaceAzimuthDeg, double ambientC,
        double pdc0, double dcAcRatio = 1.2, double systemLosses = 0.14)
    {
        var poa = Irradiance.PlaneOfArray(irr, sun, tiltDeg, surfaceAzimuthDeg);
        double tc = CellTemperature.Noct(ambientC, poa.Global);
        double dc = DcPower(poa.Global, tc, pdc0) * (1 - systemLosses);
        return InverterAc(dc, pdc0 / dcAcRatio);
    }
}

/// <summary>Tabulated inverter efficiency curve with CEC and European weighted efficiencies.</summary>
public sealed class InverterEfficiencyCurve
{
    private readonly double[] _load, _eff;

    /// <param name="loadFraction">Output power as fraction of rating (ascending, 0–1).</param>
    /// <param name="efficiency">Efficiency (0–1) at each point.</param>
    public InverterEfficiencyCurve(double[] loadFraction, double[] efficiency)
    {
        if (loadFraction.Length != efficiency.Length || loadFraction.Length < 2) throw new ArgumentException("Need ≥ 2 matching points.");
        _load = loadFraction; _eff = efficiency;
    }

    /// <summary>A typical modern string inverter (peak ≈ 98 %).</summary>
    public static InverterEfficiencyCurve Typical { get; } =
        new([0.05, 0.10, 0.20, 0.30, 0.50, 0.75, 1.00], [0.920, 0.955, 0.972, 0.977, 0.980, 0.979, 0.976]);

    /// <summary>Efficiency at a load fraction (linear interpolation, clamped).</summary>
    public double Efficiency(double loadFraction)
    {
        if (loadFraction <= 0) return 0;
        if (loadFraction <= _load[0]) return _eff[0];
        if (loadFraction >= _load[^1]) return _eff[^1];
        int i = Array.BinarySearch(_load, loadFraction);
        if (i >= 0) return _eff[i];
        i = ~i;
        double f = (loadFraction - _load[i - 1]) / (_load[i] - _load[i - 1]);
        return _eff[i - 1] + f * (_eff[i] - _eff[i - 1]);
    }

    /// <summary>CEC weighted efficiency: weights 0.04/0.05/0.12/0.21/0.53/0.05 at 10/20/30/50/75/100 % load.</summary>
    public double CecWeighted() =>
        0.04 * Efficiency(0.10) + 0.05 * Efficiency(0.20) + 0.12 * Efficiency(0.30) + 0.21 * Efficiency(0.50) + 0.53 * Efficiency(0.75) + 0.05 * Efficiency(1.0);

    /// <summary>European weighted efficiency: weights 0.03/0.06/0.13/0.10/0.48/0.20 at 5/10/20/30/50/100 % load.</summary>
    public double EuroWeighted() =>
        0.03 * Efficiency(0.05) + 0.06 * Efficiency(0.10) + 0.13 * Efficiency(0.20) + 0.10 * Efficiency(0.30) + 0.48 * Efficiency(0.50) + 0.20 * Efficiency(1.0);
}

/// <summary>
/// Sandia grid-connected inverter model (King et al. 2007):
/// Pac = (Paco/(A−B) − C(A−B))(Pdc−B) + C(Pdc−B)², A = Pdco(1+C1ΔV), B = Pso(1+C2ΔV), C = C0(1+C3ΔV), ΔV = Vdc − Vdco.
/// All powers in W, voltages in V.
/// </summary>
public sealed record SandiaInverter(double Paco, double Pdco, double Vdco, double Pso, double C0, double C1 = 0, double C2 = 0, double C3 = 0, double Pnt = 0)
{
    public double AcPower(double pdc, double vdc)
    {
        double dv = vdc - Vdco;
        double a = Pdco * (1 + C1 * dv), b = Pso * (1 + C2 * dv), c = C0 * (1 + C3 * dv);
        if (pdc < b) return -Pnt;
        double pac = (Paco / (a - b) - c * (a - b)) * (pdc - b) + c * (pdc - b) * (pdc - b);
        return Math.Min(pac, Paco);
    }
}

/// <summary>Result of an MPPT simulation: operating voltage/power per step and tracking efficiency (ΣP / ΣP_mpp).</summary>
public sealed record MpptTrace(double[] Voltage, double[] Power, double[] MppPower, double TrackingEfficiency);

/// <summary>Maximum power point tracking algorithms.</summary>
public static class Mppt
{
    /// <summary>
    /// Perturb-and-observe MPPT on a single-diode module: each step applies the irradiance [W/m²] of that step,
    /// measures P at the current operating voltage and perturbs V by ±<paramref name="stepVolts"/> in the direction that increased power.
    /// </summary>
    public static MpptTrace PerturbAndObserve(SingleDiodeModel module, IReadOnlyList<double> irradiance, double cellTempC = 25,
        double startVoltage = double.NaN, double stepVolts = 0.2)
    {
        int n = irradiance.Count;
        var v = new double[n]; var p = new double[n]; var pm = new double[n];
        double vop = double.IsNaN(startVoltage) ? 0.7 * module.Datasheet.Voc : startVoltage;
        double prevP = 0, prevV = vop - stepVolts, dir = 1;
        for (int k = 0; k < n; k++)
        {
            double g = irradiance[k];
            double power = Math.Max(0, vop * module.Current(vop, g, cellTempC));
            v[k] = vop; p[k] = power;
            pm[k] = g > 0 ? module.MaximumPowerPoint(g, cellTempC).Power : 0;
            double dP = power - prevP, dV = vop - prevV;
            if (dP != 0) dir = (dP > 0) == (dV > 0) ? 1 : -1;
            prevP = power; prevV = vop;
            vop = Math.Max(0, vop + dir * stepVolts);
        }
        double sp = p.Sum(), sm = pm.Sum();
        return new MpptTrace(v, p, pm, sm > 0 ? sp / sm : 0);
    }
}
