namespace Agritech.Crops;

/// <summary>Thermal-time (growing degree day) and crop coefficient helpers.</summary>
public static class Phenology
{
    /// <summary>Daily GDD with base temperature and upper cutoff (modified average method).</summary>
    public static double GrowingDegreeDays(double tmin, double tmax, double baseTemp, double cutoff)
    {
        double hi = Math.Min(Math.Max(tmax, baseTemp), cutoff);
        double lo = Math.Min(Math.Max(tmin, baseTemp), cutoff);
        return Math.Max(0, (hi + lo) / 2 - baseTemp);
    }

    public static double GrowingDegreeDays(this CropDefinition crop, double tmin, double tmax) =>
        GrowingDegreeDays(tmin, tmax, crop.BaseTempC, crop.CutoffTempC);

    /// <summary>FAO-56 single crop coefficient for a day after planting (linear interpolation across stages).</summary>
    public static double CropCoefficient(this CropDefinition crop, int dayAfterPlanting)
    {
        var d = crop.StageDays;
        int ini = d[0], dev = d[1], mid = d[2], late = d[3];
        if (dayAfterPlanting <= ini) return crop.KcIni;
        if (dayAfterPlanting <= ini + dev) return crop.KcIni + (crop.KcMid - crop.KcIni) * (dayAfterPlanting - ini) / (double)dev;
        if (dayAfterPlanting <= ini + dev + mid) return crop.KcMid;
        if (dayAfterPlanting <= ini + dev + mid + late)
            return crop.KcMid + (crop.KcEnd - crop.KcMid) * (dayAfterPlanting - ini - dev - mid) / (double)late;
        return crop.KcEnd;
    }

    /// <summary>Stage active at a given cumulative GDD.</summary>
    public static GrowthStage? StageAt(this CropDefinition crop, double cumulativeGdd)
    {
        if (crop.Stages.Count == 0) return null;
        double f = cumulativeGdd / crop.GddToMaturity;
        GrowthStage current = crop.Stages[0];
        foreach (var s in crop.Stages) if (f >= s.StartFraction) current = s;
        return current;
    }
}

/// <summary>Tracks a planted crop through its lifecycle using daily temperatures.</summary>
public sealed class CropLifecycle
{
    private readonly List<(DateOnly Date, double Gdd)> _history = [];

    public CropLifecycle(CropDefinition crop, DateOnly plantingDate)
    {
        Crop = crop; PlantingDate = plantingDate; CurrentDate = plantingDate;
    }

    public CropDefinition Crop { get; }
    public DateOnly PlantingDate { get; }
    public DateOnly CurrentDate { get; private set; }
    public double CumulativeGdd { get; private set; }
    public int DaysAfterPlanting => CurrentDate.DayNumber - PlantingDate.DayNumber;
    public double Progress => Math.Min(1, CumulativeGdd / Crop.GddToMaturity);
    public GrowthStage? Stage => Crop.StageAt(CumulativeGdd);
    public bool IsMature => CumulativeGdd >= Crop.GddToMaturity;
    public IReadOnlyList<(DateOnly Date, double Gdd)> History => _history;
    public double CurrentKc => Crop.CropCoefficient(DaysAfterPlanting);

    /// <summary>Advances one day with observed min/max temperature (°C).</summary>
    public void Advance(double tmin, double tmax)
    {
        double gdd = Crop.GrowingDegreeDays(tmin, tmax);
        CumulativeGdd += gdd;
        CurrentDate = CurrentDate.AddDays(1);
        _history.Add((CurrentDate, gdd));
    }

    /// <summary>Projects harvest date assuming a constant mean daily GDD going forward.</summary>
    public DateOnly EstimateHarvestDate(double expectedDailyGdd)
    {
        if (IsMature) return CurrentDate;
        if (expectedDailyGdd <= 0) throw new ArgumentOutOfRangeException(nameof(expectedDailyGdd));
        int days = (int)Math.Ceiling((Crop.GddToMaturity - CumulativeGdd) / expectedDailyGdd);
        return CurrentDate.AddDays(days);
    }
}
