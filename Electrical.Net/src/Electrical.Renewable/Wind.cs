namespace Electrical.Renewable;

/// <summary>
/// Wind turbine power curve. Either parametric (zero below cut-in, cubic interpolation of v³ between cut-in and rated,
/// rated power to cut-out) or tabulated (linear interpolation of manufacturer points). Powers in kW, speeds in m/s.
/// </summary>
public sealed class WindTurbine
{
    private readonly double[]? _speeds, _powers;

    /// <param name="ratedKw">Rated electrical power [kW].</param>
    /// <param name="rotorDiameterM">Rotor diameter [m] (for power coefficient and swept area).</param>
    /// <param name="hubHeightM">Hub height [m].</param>
    public WindTurbine(double ratedKw, double cutInMs = 3, double ratedMs = 12, double cutOutMs = 25, double rotorDiameterM = 90, double hubHeightM = 80)
    {
        if (!(cutInMs < ratedMs && ratedMs < cutOutMs)) throw new ArgumentException("Require cut-in < rated < cut-out.");
        RatedKw = ratedKw; CutInMs = cutInMs; RatedMs = ratedMs; CutOutMs = cutOutMs; RotorDiameterM = rotorDiameterM; HubHeightM = hubHeightM;
    }

    private WindTurbine(double ratedKw, double cutIn, double rated, double cutOut, double d, double h, double[] speeds, double[] powers)
        : this(ratedKw, cutIn, rated, cutOut, d, h)
    {
        _speeds = speeds; _powers = powers;
    }

    /// <summary>Tabulated power curve (speeds ascending [m/s], powers [kW]); output is zero below the first non-zero point and at/after cut-out.</summary>
    public static WindTurbine FromTable(double[] speedsMs, double[] powersKw, double cutOutMs, double rotorDiameterM = 90, double hubHeightM = 80)
    {
        if (speedsMs.Length != powersKw.Length || speedsMs.Length < 2) throw new ArgumentException("Speed/power length mismatch.");
        int first = Array.FindIndex(powersKw, p => p > 0);
        if (first < 0) throw new ArgumentException("Power curve is all zero.");
        double cutIn = first == 0 ? speedsMs[0] : speedsMs[first - 1];
        double rated = speedsMs[Array.IndexOf(powersKw, powersKw.Max())];
        return new WindTurbine(powersKw.Max(), cutIn, rated, cutOutMs, rotorDiameterM, hubHeightM, speedsMs, powersKw);
    }

    public double RatedKw { get; }
    public double CutInMs { get; }
    public double RatedMs { get; }
    public double CutOutMs { get; }
    public double RotorDiameterM { get; }
    public double HubHeightM { get; }
    /// <summary>Rotor swept area [m²].</summary>
    public double SweptAreaM2 => Math.PI * RotorDiameterM * RotorDiameterM / 4;

    /// <summary>A generic 2 MW class turbine (cut-in 3, rated 12, cut-out 25 m/s, 90 m rotor, 80 m hub).</summary>
    public static WindTurbine Generic2MW { get; } = new(2000, 3, 12, 25, 90, 80);

    /// <summary>Electrical power [kW] at hub-height wind speed [m/s] for standard air density 1.225 kg/m³.</summary>
    public double Power(double v)
    {
        if (v < CutInMs || v >= CutOutMs || v <= 0) return 0;
        if (_speeds is not null)
        {
            if (v <= _speeds[0]) return _powers![0];
            if (v >= _speeds[^1]) return _powers![^1];
            int i = Array.BinarySearch(_speeds, v);
            if (i >= 0) return _powers![i];
            i = ~i;
            double f = (v - _speeds[i - 1]) / (_speeds[i] - _speeds[i - 1]);
            return _powers![i - 1] + f * (_powers[i] - _powers[i - 1]);
        }
        if (v >= RatedMs) return RatedKw;
        double a = Math.Pow(CutInMs, 3), b = Math.Pow(RatedMs, 3);
        return RatedKw * (v * v * v - a) / (b - a);
    }

    /// <summary>Power [kW] with IEC 61400-12-1 density correction (for pitch-regulated turbines): v_n = v·(ρ/1.225)^(1/3).</summary>
    public double Power(double v, double airDensity) => Power(v * Math.Cbrt(airDensity / AirDensity.Standard));

    /// <summary>Power coefficient Cp = P / (½ρAv³) at a wind speed.</summary>
    public double PowerCoefficient(double v, double airDensity = AirDensity.Standard) =>
        v <= 0 ? 0 : Power(v, airDensity) * 1000 / (0.5 * airDensity * SweptAreaM2 * v * v * v);
}

/// <summary>Air density helpers [kg/m³].</summary>
public static class AirDensity
{
    /// <summary>ISO standard sea-level air density (15 °C, 101.325 kPa).</summary>
    public const double Standard = 1.225;

    /// <summary>Ideal-gas density ρ = p / (R_d·T) for dry air, p in Pa, T in °C.</summary>
    public static double FromTemperaturePressure(double temperatureC, double pressurePa) => pressurePa / (287.05 * (temperatureC + 273.15));

    /// <summary>Standard-atmosphere pressure [Pa] at altitude [m]: 101325·(1 − 2.25577·10⁻⁵ h)^5.25588.</summary>
    public static double PressureAtAltitude(double altitudeM) => 101325 * Math.Pow(1 - 2.25577e-5 * altitudeM, 5.25588);

    /// <summary>Density at altitude [m] and temperature [°C].</summary>
    public static double AtAltitude(double altitudeM, double temperatureC = 15) => FromTemperaturePressure(temperatureC, PressureAtAltitude(altitudeM));
}

/// <summary>Vertical wind profile extrapolation.</summary>
public static class WindShear
{
    /// <summary>Power law v(h) = v_ref·(h/h_ref)^α (α ≈ 1/7 for open terrain).</summary>
    public static double PowerLaw(double vRef, double hRefM, double hM, double alpha = 1.0 / 7) => vRef * Math.Pow(hM / hRefM, alpha);

    /// <summary>Logarithmic law v(h) = v_ref·ln(h/z0)/ln(h_ref/z0) with roughness length z0 [m] (0.03 = open farmland).</summary>
    public static double LogLaw(double vRef, double hRefM, double hM, double roughnessM = 0.03) =>
        vRef * Math.Log(hM / roughnessM) / Math.Log(hRefM / roughnessM);

    /// <summary>Shear exponent α from two measurement heights.</summary>
    public static double Exponent(double v1, double h1M, double v2, double h2M) => Math.Log(v2 / v1) / Math.Log(h2M / h1M);
}

/// <summary>Two-parameter Weibull wind-speed distribution with shape k and scale c [m/s].</summary>
public readonly record struct WeibullDistribution(double K, double C)
{
    /// <summary>Rayleigh distribution (k = 2) with the given mean speed: c = 2·v̄/√π.</summary>
    public static WeibullDistribution Rayleigh(double meanMs) => new(2, 2 * meanMs / Math.Sqrt(Math.PI));

    /// <summary>Weibull with given shape and mean speed: c = v̄ / Γ(1 + 1/k).</summary>
    public static WeibullDistribution FromMean(double meanMs, double k) => new(k, meanMs / SpecialFunctions.Gamma(1 + 1 / k));

    public double Pdf(double v) => v < 0 ? 0 : K / C * Math.Pow(v / C, K - 1) * Math.Exp(-Math.Pow(v / C, K));
    public double Cdf(double v) => v <= 0 ? 0 : 1 - Math.Exp(-Math.Pow(v / C, K));
    /// <summary>Mean speed c·Γ(1 + 1/k) [m/s].</summary>
    public double Mean => C * SpecialFunctions.Gamma(1 + 1 / K);
    /// <summary>E[v³] = c³·Γ(1 + 3/k).</summary>
    public double MeanCube => C * C * C * SpecialFunctions.Gamma(1 + 3 / K);
    /// <summary>Mean wind power density ½ρ·E[v³] [W/m²].</summary>
    public double PowerDensity(double airDensity = AirDensity.Standard) => 0.5 * airDensity * MeanCube;

    /// <summary>Maximum-likelihood-free moment fit: k = (σ/v̄)^−1.086 (Justus), c = v̄/Γ(1+1/k).</summary>
    public static WeibullDistribution Fit(IReadOnlyList<double> speeds)
    {
        double m = speeds.Average();
        double sd = Math.Sqrt(speeds.Sum(v => (v - m) * (v - m)) / Math.Max(1, speeds.Count - 1));
        double k = Math.Pow(sd / m, -1.086);
        return FromMean(m, k);
    }
}

/// <summary>Annual energy production estimate.</summary>
/// <param name="AepMwh">Net annual energy [MWh/yr].</param>
/// <param name="CapacityFactor">AEP / (rated × 8760).</param>
/// <param name="MeanPowerKw">Mean electrical power [kW].</param>
/// <param name="FullLoadHours">AEP / rated [h/yr].</param>
public sealed record AnnualEnergy(double AepMwh, double CapacityFactor, double MeanPowerKw, double FullLoadHours);

/// <summary>Wind energy yield from a wind-speed distribution and power curve.</summary>
public static class WindEnergy
{
    /// <summary>
    /// AEP = 8760·∫P(v)f(v)dv·availability·(1 − losses), integrated with Simpson's rule on [0, 40] m/s.
    /// </summary>
    public static AnnualEnergy AnnualEnergyProduction(WindTurbine turbine, WeibullDistribution wind, double airDensity = AirDensity.Standard,
        double availability = 1.0, double losses = 0.0)
    {
        const int n = 4000; const double vmax = 40;
        double h = vmax / n, s = 0;
        for (int i = 0; i <= n; i++)
        {
            double v = i * h;
            double w = i == 0 || i == n ? 1 : (i % 2 == 1 ? 4 : 2);
            s += w * turbine.Power(v, airDensity) * wind.Pdf(v);
        }
        double meanKw = s * h / 3 * availability * (1 - losses);
        double aep = meanKw * 8760 / 1000;
        return new AnnualEnergy(aep, meanKw / turbine.RatedKw, meanKw, aep * 1000 / turbine.RatedKw);
    }

    /// <summary>Energy [MWh] from a wind-speed time series [m/s] sampled every <paramref name="stepHours"/>.</summary>
    public static double EnergyFromSeries(WindTurbine turbine, IReadOnlyList<double> speeds, double stepHours = 1, double airDensity = AirDensity.Standard) =>
        speeds.Sum(v => turbine.Power(v, airDensity)) * stepHours / 1000;
}

/// <summary>Special functions used by the renewable models.</summary>
public static class SpecialFunctions
{
    private static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    /// <summary>Gamma function Γ(x) (Lanczos approximation, g = 7; ≈15 significant digits).</summary>
    public static double Gamma(double x)
    {
        if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1 - x));
        x -= 1;
        double a = Lanczos[0];
        double t = x + 7.5;
        for (int i = 1; i < 9; i++) a += Lanczos[i] / (x + i);
        return Math.Sqrt(2 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * a;
    }
}
