namespace Agritech.Crops;

/// <summary>Yield estimation methods.</summary>
public static class YieldEstimator
{
    /// <summary>
    /// FAO-33 / AquaCrop water production function: 1 − Ya/Ym = Ky·(1 − ETa/ETm).
    /// </summary>
    /// <param name="crop">Crop definition (provides Ky and potential yield).</param>
    /// <param name="actualEt">Seasonal actual evapotranspiration (mm).</param>
    /// <param name="maxEt">Seasonal maximum (non-stressed) crop ET (mm).</param>
    /// <param name="potentialYield">Override potential yield (t/ha); defaults to the crop's value.</param>
    public static double WaterLimited(CropDefinition crop, double actualEt, double maxEt, double? potentialYield = null)
    {
        double ym = potentialYield ?? crop.PotentialYieldTonHa;
        if (maxEt <= 0) return 0;
        double ratio = Math.Clamp(actualEt / maxEt, 0, 1);
        return Math.Max(0, ym * (1 - crop.YieldResponseFactorKy * (1 - ratio)));
    }

    /// <summary>Yield (t/ha) from above-ground biomass (t/ha) × harvest index.</summary>
    public static double FromBiomass(CropDefinition crop, double biomassTonHa) => biomassTonHa * crop.HarvestIndex;

    /// <summary>
    /// Empirical NDVI-based estimate: scales potential yield by peak-season NDVI relative to a reference
    /// healthy canopy (default 0.85), floored at soil-background NDVI (0.15).
    /// </summary>
    public static double FromPeakNdvi(CropDefinition crop, double peakNdvi, double referenceNdvi = 0.85, double soilNdvi = 0.15)
    {
        double f = Math.Clamp((peakNdvi - soilNdvi) / (referenceNdvi - soilNdvi), 0, 1.05);
        return crop.PotentialYieldTonHa * f;
    }

    /// <summary>Yield-gap analysis returning absolute (t/ha) and relative gap.</summary>
    public static (double Gap, double Percent) YieldGap(double potential, double actual) =>
        (Math.Max(0, potential - actual), potential <= 0 ? 0 : Math.Max(0, potential - actual) / potential * 100);
}
