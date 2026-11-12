using AquaCulture.Native;

namespace AquaCulture.WaterQuality;

/// <summary>Aquatic chemistry used by assessment, alerts and recommendations.</summary>
public static class WaterChemistry
{
    /// <summary>Fraction of TAN present as toxic un-ionised NH₃ (Emerson et al. 1975; salinity per Bower &amp; Bidwell 1978).</summary>
    public static double UnionizedAmmoniaFraction(double tempC, double ph, double salinityPpt = 0) => AquaKernels.Nh3Fraction(tempC, ph, salinityPpt);

    /// <summary>Un-ionised ammonia NH₃-N (mg/L) from TAN.</summary>
    public static double UnionizedAmmonia(double tanMgL, double tempC, double ph, double salinityPpt = 0) => tanMgL * UnionizedAmmoniaFraction(tempC, ph, salinityPpt);

    /// <summary>Oxygen solubility (mg/L) at temperature, salinity and altitude.</summary>
    public static double OxygenSaturation(double tempC, double salinityPpt = 0, double altitudeM = 0) =>
        AquaKernels.DoSaturation(tempC, salinityPpt, Core.Units.PressureAtAltitudeKPa(altitudeM));

    /// <summary>DO as percent of saturation.</summary>
    public static double OxygenPercentSaturation(double doMgL, double tempC, double salinityPpt = 0, double altitudeM = 0) =>
        100 * doMgL / OxygenSaturation(tempC, salinityPpt, altitudeM);

    /// <summary>
    /// Free carbon dioxide (mg/L) from pH and alkalinity (mg/L CaCO₃) via carbonate equilibrium (freshwater constants, temperature-corrected pK1).
    /// </summary>
    public static double FreeCo2(double ph, double alkalinityMgL, double tempC)
    {
        double tk = tempC + 273.15;
        double pk1 = 3404.71 / tk - 14.8435 + 0.032786 * tk;           // Harned & Davis
        double hco3 = alkalinityMgL / 50.04 * 1e-3;                      // mol/L (bicarbonate dominated)
        double co2 = hco3 * Math.Pow(10, pk1 - ph) * 1e-0;                // mol/L
        return co2 * 44.01 * 1000;
    }

    /// <summary>Sodium bicarbonate (kg) to raise alkalinity by <paramref name="deltaMgL"/> in <paramref name="volumeM3"/> (1.68 mg NaHCO₃ per mg CaCO₃).</summary>
    public static double SodiumBicarbonateKg(double deltaMgL, double volumeM3) => Math.Max(0, deltaMgL) * 1.68 * volumeM3 / 1000;

    /// <summary>Agricultural lime (kg CaCO₃) to raise alkalinity, allowing for ~50 % dissolution efficiency.</summary>
    public static double AgriculturalLimeKg(double deltaMgL, double volumeM3, double efficiency = 0.5) => Math.Max(0, deltaMgL) * volumeM3 / 1000 / efficiency;

    /// <summary>Salt (NaCl, kg) to reach a chloride : nitrite-N ratio that blocks nitrite uptake in fish (default 20 : 1).</summary>
    public static double SaltForNitriteKg(double nitriteN, double currentChlorideMgL, double volumeM3, double ratio = 20)
    {
        double needCl = Math.Max(0, ratio * nitriteN - currentChlorideMgL);
        return needCl / 0.6066 * volumeM3 / 1000;
    }

    /// <summary>Chloride (mg/L) of brackish water from salinity (ppt), Knudsen relation.</summary>
    public static double ChlorideFromSalinity(double salinityPpt) => salinityPpt / 1.80655 * 1000;

    /// <summary>
    /// Fraction of pond volume to exchange to bring a conservative substance from <paramref name="current"/> to <paramref name="target"/>
    /// with inflow concentration <paramref name="inflow"/> (complete mixing).
    /// </summary>
    public static double ExchangeFraction(double current, double target, double inflow = 0)
    {
        if (current == inflow) return 0;
        double f = (current - target) / (current - inflow);
        return Math.Clamp(f, 0, 1);
    }

    /// <summary>Molasses (kg) to immobilise excess TAN in heterotrophic (biofloc) systems: ~20 g carbohydrate per g TAN, molasses ≈ 60 % sugar.</summary>
    public static double MolassesForTanKg(double excessTanMgL, double volumeM3, double carbPerTan = 20, double sugarFraction = 0.6) =>
        Math.Max(0, excessTanMgL) * volumeM3 * carbPerTan / sugarFraction / 1000;
}
