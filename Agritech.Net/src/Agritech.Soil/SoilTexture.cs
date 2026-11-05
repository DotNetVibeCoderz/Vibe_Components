namespace Agritech.Soil;

public enum SoilTextureClass
{
    Sand, LoamySand, SandyLoam, Loam, SiltLoam, Silt, SandyClayLoam, ClayLoam, SiltyClayLoam, SandyClay, SiltyClay, Clay
}

/// <summary>USDA texture triangle classification and Saxton &amp; Rawls (2006) pedotransfer functions.</summary>
public static class SoilTexture
{
    public static SoilTextureClass Classify(double sand, double silt, double clay)
    {
        double total = sand + silt + clay;
        if (total <= 0) throw new ArgumentException("Fractions must sum to a positive value.");
        sand = sand / total * 100; silt = silt / total * 100; clay = clay / total * 100;

        if (silt + 1.5 * clay < 15) return SoilTextureClass.Sand;
        if (silt + 1.5 * clay >= 15 && silt + 2 * clay < 30) return SoilTextureClass.LoamySand;
        if ((clay >= 7 && clay < 20 && sand > 52 && silt + 2 * clay >= 30) || (clay < 7 && silt < 50 && silt + 2 * clay >= 30))
            return SoilTextureClass.SandyLoam;
        if (clay >= 7 && clay < 27 && silt >= 28 && silt < 50 && sand <= 52) return SoilTextureClass.Loam;
        if ((silt >= 50 && clay >= 12 && clay < 27) || (silt >= 50 && silt < 80 && clay < 12)) return SoilTextureClass.SiltLoam;
        if (silt >= 80 && clay < 12) return SoilTextureClass.Silt;
        if (clay >= 20 && clay < 35 && silt < 28 && sand > 45) return SoilTextureClass.SandyClayLoam;
        if (clay >= 27 && clay < 40 && sand > 20 && sand <= 45) return SoilTextureClass.ClayLoam;
        if (clay >= 27 && clay < 40 && sand <= 20) return SoilTextureClass.SiltyClayLoam;
        if (clay >= 35 && sand > 45) return SoilTextureClass.SandyClay;
        if (clay >= 40 && silt >= 40) return SoilTextureClass.SiltyClay;
        return SoilTextureClass.Clay;
    }

    /// <summary>Hydraulic properties (volumetric, m³/m³) estimated from texture and organic matter.</summary>
    public static SoilHydraulics Hydraulics(double sandPct, double clayPct, double organicMatterPct)
    {
        double s = sandPct / 100, c = clayPct / 100, om = Math.Clamp(organicMatterPct, 0, 8);
        // Saxton & Rawls 2006, eqs. 1-2
        double t1500 = -0.024 * s + 0.487 * c + 0.006 * om + 0.005 * s * om - 0.013 * c * om + 0.068 * s * c + 0.031;
        double wp = t1500 + (0.14 * t1500 - 0.02);
        double t33 = -0.251 * s + 0.195 * c + 0.011 * om + 0.006 * s * om - 0.027 * c * om + 0.452 * s * c + 0.299;
        double fc = t33 + (1.283 * t33 * t33 - 0.374 * t33 - 0.015);
        double ts33 = 0.278 * s + 0.034 * c + 0.022 * om - 0.018 * s * om - 0.027 * c * om - 0.584 * s * c + 0.078;
        double s33 = ts33 + (0.636 * ts33 - 0.107);
        double sat = fc + s33 - 0.097 * s + 0.043;
        // Ksat (mm/h), eqs. 15-16
        double b = (Math.Log(1500) - Math.Log(33)) / (Math.Log(fc) - Math.Log(wp));
        double lambda = 1 / b;
        double ksat = 1930 * Math.Pow(Math.Max(sat - fc, 1e-6), 3 - lambda);
        return new SoilHydraulics(Math.Max(0.01, wp), fc, sat, ksat);
    }

    public static SoilHydraulics Hydraulics(this SoilSample s) => Hydraulics(s.SandPct, s.ClayPct, s.OrganicMatterPct);
}

/// <param name="WiltingPoint">Volumetric water at −1500 kPa.</param>
/// <param name="FieldCapacity">Volumetric water at −33 kPa.</param>
/// <param name="Saturation">Saturated volumetric water content.</param>
/// <param name="KsatMmPerHour">Saturated hydraulic conductivity.</param>
public readonly record struct SoilHydraulics(double WiltingPoint, double FieldCapacity, double Saturation, double KsatMmPerHour)
{
    /// <summary>Total available water per metre of soil (mm/m).</summary>
    public double AvailableWaterMmPerM => (FieldCapacity - WiltingPoint) * 1000;
    public double TotalAvailableWater(double rootDepthM) => AvailableWaterMmPerM * rootDepthM;
}
