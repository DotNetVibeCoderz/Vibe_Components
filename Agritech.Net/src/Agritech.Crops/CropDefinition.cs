namespace Agritech.Crops;

/// <summary>Agronomic parameters for a crop. Coefficients follow FAO-56 (Kc), FAO-33 (Ky) and common extension literature.</summary>
public sealed record CropDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Indonesian common name.</summary>
    public required string NameId { get; init; }
    public required string ScientificName { get; init; }
    public CropCategory Category { get; init; }

    // Thermal
    public double BaseTempC { get; init; } = 10;
    public double CutoffTempC { get; init; } = 30;
    public double OptimalTempMinC { get; init; } = 20;
    public double OptimalTempMaxC { get; init; } = 30;
    public double AbsoluteTempMinC { get; init; } = 10;
    public double AbsoluteTempMaxC { get; init; } = 40;
    public double GddToMaturity { get; init; } = 1500;

    // Site suitability
    public double RainfallMinMm { get; init; } = 400;
    public double RainfallOptMinMm { get; init; } = 600;
    public double RainfallOptMaxMm { get; init; } = 1200;
    public double RainfallMaxMm { get; init; } = 2500;
    public double PhMin { get; init; } = 5.0;
    public double PhOptMin { get; init; } = 5.8;
    public double PhOptMax { get; init; } = 7.0;
    public double PhMax { get; init; } = 8.0;
    public double ElevationMaxM { get; init; } = 1500;

    // Water (FAO-56)
    public double KcIni { get; init; } = 0.4;
    public double KcMid { get; init; } = 1.1;
    public double KcEnd { get; init; } = 0.6;
    /// <summary>Stage lengths in days: initial, development, mid-season, late-season.</summary>
    public int[] StageDays { get; init; } = [20, 30, 40, 20];
    public double YieldResponseFactorKy { get; init; } = 1.0;
    public double MaxRootDepthM { get; init; } = 1.0;
    /// <summary>Fraction of TAW that can be depleted before water stress (p).</summary>
    public double DepletionFraction { get; init; } = 0.5;

    // Growth & yield
    public double PotentialYieldTonHa { get; init; } = 5;
    public double HarvestIndex { get; init; } = 0.45;
    /// <summary>Radiation use efficiency (g dry matter per MJ intercepted PAR).</summary>
    public double RueGPerMj { get; init; } = 2.5;
    public double ExtinctionCoefficient { get; init; } = 0.6;
    public double MaxLai { get; init; } = 5;

    /// <summary>Nutrient removal per tonne of harvested product (kg N, P2O5, K2O).</summary>
    public NutrientRemoval Removal { get; init; } = new(20, 8, 20);

    public IReadOnlyList<GrowthStage> Stages { get; init; } = [];

    public int SeasonLengthDays => StageDays.Sum();
}

public enum CropCategory { Cereal, Legume, RootTuber, Vegetable, Fruit, Plantation, Industrial, Forage }

public readonly record struct NutrientRemoval(double N, double P2O5, double K2O);

/// <summary>Phenological stage defined by the GDD fraction at which it begins.</summary>
public sealed record GrowthStage(string Code, string Name, string NameId, double StartFraction);
