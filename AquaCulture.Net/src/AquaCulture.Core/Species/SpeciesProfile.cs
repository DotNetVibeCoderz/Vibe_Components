namespace AquaCulture.Core.Species;

public enum SpeciesGroup { Fish, Shrimp }

public enum Habitat { Freshwater, Brackish, Marine, Euryhaline }

/// <summary>A tolerance band: lethal/stress limits outside an optimal window.</summary>
/// <param name="Min">Lower tolerance limit (stress begins below <see cref="OptLow"/>, critical below Min).</param>
/// <param name="OptLow">Lower bound of the optimal range.</param>
/// <param name="OptHigh">Upper bound of the optimal range.</param>
/// <param name="Max">Upper tolerance limit.</param>
public readonly record struct ToleranceRange(double Min, double OptLow, double OptHigh, double Max)
{
    public bool IsOptimal(double v) => v >= OptLow && v <= OptHigh;
    public bool IsTolerable(double v) => v >= Min && v <= Max;

    /// <summary>Piecewise-linear suitability 0–1: 1 inside the optimum, falling to 0 at the tolerance limits.</summary>
    public double Suitability(double v)
    {
        if (double.IsNaN(v)) return double.NaN;
        if (v >= OptLow && v <= OptHigh) return 1;
        if (v < OptLow) return v <= Min ? 0 : (v - Min) / (OptLow - Min);
        return v >= Max ? 0 : (Max - v) / (Max - OptHigh);
    }
}

/// <summary>
/// Biological and husbandry profile of a cultured species. Values are literature/industry midpoints for tropical
/// (Indonesian) farming and are meant as defaults that you can override per farm.
/// </summary>
public sealed record SpeciesProfile
{
    public required string Id { get; init; }
    public required Text2 Name { get; init; }
    public required string ScientificName { get; init; }
    public SpeciesGroup Group { get; init; }
    public Habitat Habitat { get; init; }

    /// <summary>Water temperature tolerance (°C).</summary>
    public ToleranceRange Temperature { get; init; }
    /// <summary>pH tolerance.</summary>
    public ToleranceRange Ph { get; init; }
    /// <summary>Salinity tolerance (ppt).</summary>
    public ToleranceRange Salinity { get; init; }
    /// <summary>Alkalinity tolerance (mg/L as CaCO₃).</summary>
    public ToleranceRange Alkalinity { get; init; }

    /// <summary>Critical dissolved oxygen (mg/L): sustained values below this kill or severely stress the stock.</summary>
    public double DoCritical { get; init; }
    /// <summary>Dissolved oxygen at or above which growth is not limited (mg/L).</summary>
    public double DoOptimal { get; init; }
    /// <summary>Safe limit of un-ionised ammonia NH₃-N (mg/L).</summary>
    public double Nh3Max { get; init; }
    /// <summary>Safe limit of total ammonia nitrogen (mg/L) at neutral pH.</summary>
    public double TanMax { get; init; }
    /// <summary>Safe limit of nitrite NO₂-N (mg/L).</summary>
    public double NitriteMax { get; init; }
    /// <summary>Upper turbidity limit (NTU).</summary>
    public double TurbidityMax { get; init; } = 60;

    /// <summary>Thermal-unit growth coefficient (TGC × 1000, g^(1/3) °C⁻¹ day⁻¹).</summary>
    public double Tgc { get; init; }
    /// <summary>von Bertalanffy asymptotic length (cm).</summary>
    public double LInfinityCm { get; init; }
    /// <summary>von Bertalanffy growth coefficient K (year⁻¹).</summary>
    public double VbK { get; init; }
    public double VbT0 { get; init; } = -0.1;
    /// <summary>Length–weight relationship W(g) = a·L(cm)^b.</summary>
    public double LwA { get; init; }
    public double LwB { get; init; } = 3.0;

    public double TypicalFcr { get; init; }
    public double StockingWeightG { get; init; }
    public double MarketWeightG { get; init; }
    public int TypicalCycleDays { get; init; }
    /// <summary>Typical stocking density (animals per m² for ponds; per m³ for tanks/cages).</summary>
    public double TypicalDensity { get; init; }
    /// <summary>Maximum sustainable biomass density in semi-intensive ponds (kg/m³).</summary>
    public double MaxBiomassKgM3 { get; init; }
    /// <summary>Typical survival over a full cycle (0–1).</summary>
    public double TypicalSurvival { get; init; }
    /// <summary>Indicative farm-gate price (IDR/kg).</summary>
    public double PricePerKg { get; init; }
    /// <summary>Dietary crude-protein requirement (%).</summary>
    public double ProteinRequirementPct { get; init; }
    /// <summary>Feeding rate (% body weight per day) by mean body weight (g), at optimal temperature.</summary>
    public IReadOnlyList<(double WeightG, double PercentBw)> FeedingTable { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Weight (g) of an animal of length L (cm).</summary>
    public double WeightFromLength(double lengthCm) => LwA * Math.Pow(lengthCm, LwB);

    /// <summary>Length (cm) of an animal of weight W (g).</summary>
    public double LengthFromWeight(double weightG) => Math.Pow(weightG / LwA, 1 / LwB);

    /// <summary>Feeding rate (% BW/day) interpolated (log-weight) from the feeding table.</summary>
    public double FeedingRatePercent(double weightG)
    {
        var t = FeedingTable;
        if (t.Count == 0) return 3;
        if (weightG <= t[0].WeightG) return t[0].PercentBw;
        if (weightG >= t[^1].WeightG) return t[^1].PercentBw;
        for (int i = 1; i < t.Count; i++)
            if (weightG <= t[i].WeightG)
            {
                double x0 = Math.Log(t[i - 1].WeightG), x1 = Math.Log(t[i].WeightG), f = (Math.Log(weightG) - x0) / (x1 - x0);
                return t[i - 1].PercentBw + f * (t[i].PercentBw - t[i - 1].PercentBw);
            }
        return t[^1].PercentBw;
    }

    public override string ToString() => $"{Name.En} ({ScientificName})";
}
