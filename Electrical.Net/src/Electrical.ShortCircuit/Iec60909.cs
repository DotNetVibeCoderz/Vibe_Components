namespace Electrical.ShortCircuit;

/// <summary>Nominal-voltage tolerance of low-voltage systems, selecting the IEC 60909-0 Table 1 voltage factor.</summary>
public enum LowVoltageTolerance
{
    /// <summary>Systems with a tolerance of +6 % (cmax = 1.05, cmin = 0.95).</summary>
    SixPercent,
    /// <summary>Systems with a tolerance of +10 % (cmax = 1.10, cmin = 0.90).</summary>
    TenPercent,
}

/// <summary>Closed-form factors of IEC 60909-0:2016 (voltage factor, peak factor κ, thermal factor m, decay factor μ, correction factor K_G).</summary>
public static class Iec60909
{
    /// <summary>Voltage factor c (IEC 60909-0 Table 1) for nominal system voltage <paramref name="unKv"/> [kV].</summary>
    /// <param name="unKv">Nominal line-to-line voltage [kV].</param>
    /// <param name="maximum">true = cmax (maximum short-circuit currents), false = cmin.</param>
    /// <param name="lvTolerance">Tolerance class for low-voltage (≤ 1 kV) systems.</param>
    public static double VoltageFactor(double unKv, bool maximum = true, LowVoltageTolerance lvTolerance = LowVoltageTolerance.SixPercent)
    {
        if (unKv <= 1.0)
            return lvTolerance == LowVoltageTolerance.SixPercent ? (maximum ? 1.05 : 0.95) : (maximum ? 1.10 : 0.90);
        return maximum ? 1.10 : 1.00;
    }

    /// <summary>Peak factor κ = 1.02 + 0.98·e^(−3R/X) (IEC 60909-0 eq. 55).</summary>
    public static double Kappa(double rOverX) => 1.02 + 0.98 * Math.Exp(-3 * Math.Max(rOverX, 0));

    /// <summary>Factor m for the heat effect of the DC component (IEC 60909-0 eq. 101).</summary>
    /// <param name="kappa">Peak factor κ.</param>
    /// <param name="frequencyHz">System frequency f [Hz].</param>
    /// <param name="durationS">Short-circuit duration T_k [s].</param>
    public static double ThermalFactorM(double kappa, double frequencyHz, double durationS)
    {
        if (kappa <= 1.0 + 1e-12 || durationS <= 0) return 0;
        double ln = Math.Log(kappa - 1);
        return 1 / (2 * frequencyHz * durationS * ln) * (Math.Exp(4 * frequencyHz * durationS * ln) - 1);
    }

    /// <summary>Thermal equivalent short-circuit current I_th = I″k·√(m + n) [same unit as <paramref name="ikA"/>].</summary>
    /// <param name="n">AC decay factor n (1 for far-from-generator short circuits).</param>
    public static double ThermalEquivalentCurrent(double ikA, double kappa, double frequencyHz, double durationS, double n = 1) =>
        ikA * Math.Sqrt(ThermalFactorM(kappa, frequencyHz, durationS) + n);

    /// <summary>
    /// Decay factor μ for the symmetrical breaking current of a near-to-generator short circuit, I_b = μ·I″kG (IEC 60909-0 eq. 70).
    /// </summary>
    /// <param name="ikgOverIrg">Ratio I″kG / I_rG of the generator's initial short-circuit current to its rated current.</param>
    /// <param name="minimumTimeDelayS">Minimum time delay t_min [s] (0.02, 0.05, 0.10 or ≥ 0.25; intermediate values interpolate linearly).</param>
    public static double DecayFactorMu(double ikgOverIrg, double minimumTimeDelayS)
    {
        if (ikgOverIrg <= 2) return 1.0;
        static double At(double t, double x) => t switch
        {
            <= 0.02 => 0.84 + 0.26 * Math.Exp(-0.26 * x),
            <= 0.05 => 0.71 + 0.51 * Math.Exp(-0.30 * x),
            <= 0.10 => 0.62 + 0.72 * Math.Exp(-0.32 * x),
            _ => 0.56 + 0.94 * Math.Exp(-0.38 * x),
        };
        double[] ts = [0.02, 0.05, 0.10, 0.25];
        double t = Math.Clamp(minimumTimeDelayS, 0.02, 0.25);
        for (int k = 0; k < ts.Length - 1; k++)
        {
            if (t <= ts[k + 1])
            {
                double a = At(ts[k], ikgOverIrg), b = At(ts[k + 1], ikgOverIrg);
                double w = (t - ts[k]) / (ts[k + 1] - ts[k]);
                return Math.Min(1, a + w * (b - a));
            }
        }
        return Math.Min(1, At(0.25, ikgOverIrg));
    }

    /// <summary>
    /// Generator impedance correction factor K_G = (U_n/U_rG)·c_max / (1 + x″d·sin φ_rG) (IEC 60909-0 eq. 18).
    /// </summary>
    /// <param name="cMax">Voltage factor c_max.</param>
    /// <param name="xdSubtransientPu">Saturated subtransient reactance x″d [pu on machine rating].</param>
    /// <param name="ratedPowerFactor">Rated power factor cos φ_rG.</param>
    /// <param name="unOverUrg">Ratio of system nominal voltage to generator rated voltage.</param>
    public static double GeneratorCorrectionFactor(double cMax, double xdSubtransientPu, double ratedPowerFactor = 0.85, double unOverUrg = 1.0)
    {
        double sinPhi = Math.Sqrt(Math.Max(0, 1 - ratedPowerFactor * ratedPowerFactor));
        return unOverUrg * cMax / (1 + xdSubtransientPu * sinPhi);
    }

    /// <summary>DC (aperiodic) component i_DC = √2·I″k·e^(−2π·f·t·R/X) [same unit as <paramref name="ikA"/>] (IEC 60909-0 eq. 64).</summary>
    public static double DcComponent(double ikA, double rOverX, double frequencyHz, double timeS) =>
        Math.Sqrt(2) * ikA * Math.Exp(-2 * Math.PI * frequencyHz * timeS * rOverX);
}
