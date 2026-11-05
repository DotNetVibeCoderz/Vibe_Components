using System.Collections.Frozen;

namespace Agritech.Crops;

/// <summary>Catalog of crop definitions. Ships with common tropical and temperate crops; extend via <see cref="Register"/>.</summary>
public sealed class CropCatalog
{
    private readonly Dictionary<string, CropDefinition> _crops = new(StringComparer.OrdinalIgnoreCase);
    private FrozenDictionary<string, CropDefinition>? _frozen;

    public CropCatalog(bool includeDefaults = true)
    {
        if (includeDefaults) foreach (var c in Defaults()) _crops[c.Id] = c;
    }

    public static CropCatalog Default { get; } = new();

    public IReadOnlyCollection<CropDefinition> All => Lookup.Values;

    private FrozenDictionary<string, CropDefinition> Lookup => _frozen ??= _crops.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public CropDefinition Get(string id) =>
        TryGet(id, out var c) ? c : throw new KeyNotFoundException($"Unknown crop '{id}'. Known: {string.Join(", ", _crops.Keys)}");

    /// <summary>Finds by id, English or Indonesian name.</summary>
    public bool TryGet(string idOrName, out CropDefinition crop)
    {
        if (Lookup.TryGetValue(idOrName, out crop!)) return true;
        crop = Lookup.Values.FirstOrDefault(c =>
            c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || c.NameId.Equals(idOrName, StringComparison.OrdinalIgnoreCase))!;
        return crop is not null;
    }

    public void Register(CropDefinition crop)
    {
        _crops[crop.Id] = crop;
        _frozen = null;
    }

    // Properties (not fields) so they are valid during static initialisation of Default.
    private static GrowthStage[] CerealStages =>
    [
        new("VE", "Emergence", "Perkecambahan", 0.0),
        new("V", "Vegetative", "Vegetatif", 0.08),
        new("R1", "Flowering", "Pembungaan", 0.50),
        new("R3", "Grain filling", "Pengisian biji", 0.65),
        new("R6", "Maturity", "Masak fisiologis", 1.0),
    ];

    private static GrowthStage[] GenericStages =>
    [
        new("EST", "Establishment", "Penanaman", 0.0),
        new("VEG", "Vegetative", "Vegetatif", 0.15),
        new("FLW", "Flowering", "Pembungaan", 0.45),
        new("FRT", "Fruiting / bulking", "Pembuahan / pembesaran", 0.60),
        new("MAT", "Maturity", "Panen", 1.0),
    ];

    private static IEnumerable<CropDefinition> Defaults() =>
    [
        new() { Id = "rice", Name = "Rice (paddy)", NameId = "Padi", ScientificName = "Oryza sativa", Category = CropCategory.Cereal,
            BaseTempC = 10, CutoffTempC = 35, OptimalTempMinC = 22, OptimalTempMaxC = 32, AbsoluteTempMinC = 12, AbsoluteTempMaxC = 40, GddToMaturity = 2000,
            RainfallMinMm = 800, RainfallOptMinMm = 1200, RainfallOptMaxMm = 2500, RainfallMaxMm = 4000, PhMin = 4.5, PhOptMin = 5.5, PhOptMax = 7.0, PhMax = 8.0, ElevationMaxM = 1500,
            KcIni = 1.05, KcMid = 1.20, KcEnd = 0.90, StageDays = [30, 30, 60, 30], YieldResponseFactorKy = 1.1, MaxRootDepthM = 0.5, DepletionFraction = 0.2,
            PotentialYieldTonHa = 8, HarvestIndex = 0.48, RueGPerMj = 2.4, ExtinctionCoefficient = 0.5, MaxLai = 6, Removal = new(16, 7, 20), Stages = CerealStages },
        new() { Id = "maize", Name = "Maize", NameId = "Jagung", ScientificName = "Zea mays", Category = CropCategory.Cereal,
            BaseTempC = 10, CutoffTempC = 30, OptimalTempMinC = 20, OptimalTempMaxC = 32, AbsoluteTempMinC = 10, AbsoluteTempMaxC = 42, GddToMaturity = 1500,
            RainfallMinMm = 400, RainfallOptMinMm = 500, RainfallOptMaxMm = 1200, RainfallMaxMm = 2000, PhMin = 5.0, PhOptMin = 5.8, PhOptMax = 7.0, PhMax = 8.0, ElevationMaxM = 2000,
            KcIni = 0.30, KcMid = 1.20, KcEnd = 0.60, StageDays = [20, 35, 40, 30], YieldResponseFactorKy = 1.25, MaxRootDepthM = 1.4, DepletionFraction = 0.55,
            PotentialYieldTonHa = 10, HarvestIndex = 0.50, RueGPerMj = 3.3, ExtinctionCoefficient = 0.65, MaxLai = 5.5, Removal = new(15, 7, 5), Stages = CerealStages },
        new() { Id = "wheat", Name = "Wheat", NameId = "Gandum", ScientificName = "Triticum aestivum", Category = CropCategory.Cereal,
            BaseTempC = 0, CutoffTempC = 26, OptimalTempMinC = 15, OptimalTempMaxC = 23, AbsoluteTempMinC = 3, AbsoluteTempMaxC = 34, GddToMaturity = 2100,
            RainfallMinMm = 300, RainfallOptMinMm = 450, RainfallOptMaxMm = 900, RainfallMaxMm = 1600, PhMin = 5.5, PhOptMin = 6.0, PhOptMax = 7.5, PhMax = 8.5, ElevationMaxM = 3000,
            KcIni = 0.40, KcMid = 1.15, KcEnd = 0.30, StageDays = [30, 140, 40, 30], YieldResponseFactorKy = 1.05, MaxRootDepthM = 1.5, DepletionFraction = 0.55,
            PotentialYieldTonHa = 7, HarvestIndex = 0.45, RueGPerMj = 2.8, MaxLai = 6, Removal = new(25, 10, 6), Stages = CerealStages },
        new() { Id = "soybean", Name = "Soybean", NameId = "Kedelai", ScientificName = "Glycine max", Category = CropCategory.Legume,
            BaseTempC = 10, CutoffTempC = 30, OptimalTempMinC = 20, OptimalTempMaxC = 30, GddToMaturity = 1300,
            RainfallMinMm = 350, RainfallOptMinMm = 450, RainfallOptMaxMm = 900, RainfallMaxMm = 1500, PhMin = 5.0, PhOptMin = 6.0, PhOptMax = 7.0, PhMax = 8.0, ElevationMaxM = 1000,
            KcIni = 0.40, KcMid = 1.15, KcEnd = 0.50, StageDays = [15, 15, 40, 15], YieldResponseFactorKy = 0.85, MaxRootDepthM = 1.0, DepletionFraction = 0.5,
            PotentialYieldTonHa = 3.5, HarvestIndex = 0.40, RueGPerMj = 1.8, Removal = new(60, 15, 25), Stages = GenericStages },
        new() { Id = "cassava", Name = "Cassava", NameId = "Singkong", ScientificName = "Manihot esculenta", Category = CropCategory.RootTuber,
            BaseTempC = 12, CutoffTempC = 35, OptimalTempMinC = 22, OptimalTempMaxC = 32, AbsoluteTempMinC = 12, AbsoluteTempMaxC = 40, GddToMaturity = 4000,
            RainfallMinMm = 500, RainfallOptMinMm = 1000, RainfallOptMaxMm = 2000, RainfallMaxMm = 4000, PhMin = 4.5, PhOptMin = 5.5, PhOptMax = 6.5, PhMax = 8.0, ElevationMaxM = 1500,
            KcIni = 0.30, KcMid = 1.10, KcEnd = 0.50, StageDays = [60, 90, 120, 60], YieldResponseFactorKy = 0.7, MaxRootDepthM = 1.0, DepletionFraction = 0.35,
            PotentialYieldTonHa = 40, HarvestIndex = 0.6, RueGPerMj = 1.6, Removal = new(2.5, 1.0, 4.5), Stages = GenericStages },
        new() { Id = "potato", Name = "Potato", NameId = "Kentang", ScientificName = "Solanum tuberosum", Category = CropCategory.RootTuber,
            BaseTempC = 2, CutoffTempC = 26, OptimalTempMinC = 15, OptimalTempMaxC = 22, AbsoluteTempMinC = 5, AbsoluteTempMaxC = 30, GddToMaturity = 1600,
            RainfallMinMm = 300, RainfallOptMinMm = 500, RainfallOptMaxMm = 800, RainfallMaxMm = 1500, PhMin = 4.8, PhOptMin = 5.0, PhOptMax = 6.5, PhMax = 7.5, ElevationMaxM = 3500,
            KcIni = 0.50, KcMid = 1.15, KcEnd = 0.75, StageDays = [25, 30, 45, 30], YieldResponseFactorKy = 1.1, MaxRootDepthM = 0.5, DepletionFraction = 0.35,
            PotentialYieldTonHa = 35, HarvestIndex = 0.75, RueGPerMj = 1.25, Removal = new(3.5, 1.5, 6), Stages = GenericStages },
        new() { Id = "tomato", Name = "Tomato", NameId = "Tomat", ScientificName = "Solanum lycopersicum", Category = CropCategory.Vegetable,
            BaseTempC = 10, CutoffTempC = 30, OptimalTempMinC = 18, OptimalTempMaxC = 27, AbsoluteTempMinC = 10, AbsoluteTempMaxC = 35, GddToMaturity = 1400,
            RainfallMinMm = 400, RainfallOptMinMm = 600, RainfallOptMaxMm = 1300, RainfallMaxMm = 1800, PhMin = 5.5, PhOptMin = 6.0, PhOptMax = 7.0, PhMax = 7.5, ElevationMaxM = 2000,
            KcIni = 0.60, KcMid = 1.15, KcEnd = 0.80, StageDays = [30, 40, 40, 25], YieldResponseFactorKy = 1.05, MaxRootDepthM = 1.0, DepletionFraction = 0.4,
            PotentialYieldTonHa = 60, HarvestIndex = 0.6, RueGPerMj = 1.6, Removal = new(2.5, 1.0, 4), Stages = GenericStages },
        new() { Id = "chili", Name = "Chili pepper", NameId = "Cabai", ScientificName = "Capsicum annuum", Category = CropCategory.Vegetable,
            BaseTempC = 12, CutoffTempC = 32, OptimalTempMinC = 20, OptimalTempMaxC = 30, AbsoluteTempMinC = 12, AbsoluteTempMaxC = 38, GddToMaturity = 1700,
            RainfallMinMm = 600, RainfallOptMinMm = 900, RainfallOptMaxMm = 1500, RainfallMaxMm = 2500, PhMin = 5.0, PhOptMin = 6.0, PhOptMax = 7.0, PhMax = 7.5, ElevationMaxM = 1500,
            KcIni = 0.60, KcMid = 1.05, KcEnd = 0.90, StageDays = [30, 35, 40, 20], YieldResponseFactorKy = 1.1, MaxRootDepthM = 0.8, DepletionFraction = 0.3,
            PotentialYieldTonHa = 20, HarvestIndex = 0.5, RueGPerMj = 1.5, Removal = new(3, 1.0, 5), Stages = GenericStages },
        new() { Id = "shallot", Name = "Shallot", NameId = "Bawang merah", ScientificName = "Allium cepa var. aggregatum", Category = CropCategory.Vegetable,
            BaseTempC = 6, CutoffTempC = 30, OptimalTempMinC = 22, OptimalTempMaxC = 30, AbsoluteTempMinC = 10, AbsoluteTempMaxC = 36, GddToMaturity = 1300,
            RainfallMinMm = 350, RainfallOptMinMm = 400, RainfallOptMaxMm = 800, RainfallMaxMm = 1500, PhMin = 5.5, PhOptMin = 6.0, PhOptMax = 7.0, PhMax = 7.5, ElevationMaxM = 1000,
            KcIni = 0.70, KcMid = 1.05, KcEnd = 0.75, StageDays = [15, 20, 20, 10], YieldResponseFactorKy = 1.1, MaxRootDepthM = 0.4, DepletionFraction = 0.3,
            PotentialYieldTonHa = 18, HarvestIndex = 0.7, RueGPerMj = 1.4, Removal = new(3, 1.2, 4), Stages = GenericStages },
        new() { Id = "sugarcane", Name = "Sugarcane", NameId = "Tebu", ScientificName = "Saccharum officinarum", Category = CropCategory.Industrial,
            BaseTempC = 12, CutoffTempC = 38, OptimalTempMinC = 24, OptimalTempMaxC = 34, AbsoluteTempMinC = 15, AbsoluteTempMaxC = 42, GddToMaturity = 6000,
            RainfallMinMm = 1000, RainfallOptMinMm = 1500, RainfallOptMaxMm = 2500, RainfallMaxMm = 5000, PhMin = 4.5, PhOptMin = 5.5, PhOptMax = 7.5, PhMax = 8.5, ElevationMaxM = 1200,
            KcIni = 0.40, KcMid = 1.25, KcEnd = 0.75, StageDays = [35, 60, 190, 120], YieldResponseFactorKy = 1.2, MaxRootDepthM = 1.5, DepletionFraction = 0.65,
            PotentialYieldTonHa = 120, HarvestIndex = 0.8, RueGPerMj = 1.8, Removal = new(1.2, 0.6, 2.5), Stages = GenericStages },
        new() { Id = "oil-palm", Name = "Oil palm", NameId = "Kelapa sawit", ScientificName = "Elaeis guineensis", Category = CropCategory.Plantation,
            BaseTempC = 15, CutoffTempC = 36, OptimalTempMinC = 24, OptimalTempMaxC = 32, AbsoluteTempMinC = 18, AbsoluteTempMaxC = 40, GddToMaturity = 5500,
            RainfallMinMm = 1250, RainfallOptMinMm = 1750, RainfallOptMaxMm = 3000, RainfallMaxMm = 5000, PhMin = 4.0, PhOptMin = 5.0, PhOptMax = 6.5, PhMax = 7.5, ElevationMaxM = 500,
            KcIni = 0.95, KcMid = 1.00, KcEnd = 1.00, StageDays = [90, 90, 120, 65], YieldResponseFactorKy = 1.0, MaxRootDepthM = 1.0, DepletionFraction = 0.65,
            PotentialYieldTonHa = 30, HarvestIndex = 0.3, RueGPerMj = 1.7, Removal = new(5.5, 2.2, 9.5), Stages = GenericStages },
        new() { Id = "coffee", Name = "Coffee (robusta)", NameId = "Kopi robusta", ScientificName = "Coffea canephora", Category = CropCategory.Plantation,
            BaseTempC = 12, CutoffTempC = 32, OptimalTempMinC = 22, OptimalTempMaxC = 28, AbsoluteTempMinC = 12, AbsoluteTempMaxC = 34, GddToMaturity = 4500,
            RainfallMinMm = 1200, RainfallOptMinMm = 1500, RainfallOptMaxMm = 2500, RainfallMaxMm = 3500, PhMin = 4.5, PhOptMin = 5.3, PhOptMax = 6.5, PhMax = 7.5, ElevationMaxM = 1200,
            KcIni = 0.90, KcMid = 0.95, KcEnd = 0.95, StageDays = [60, 90, 150, 65], YieldResponseFactorKy = 1.0, MaxRootDepthM = 1.2, DepletionFraction = 0.4,
            PotentialYieldTonHa = 2.5, HarvestIndex = 0.2, RueGPerMj = 1.0, Removal = new(35, 6, 40), Stages = GenericStages },
        new() { Id = "cacao", Name = "Cacao", NameId = "Kakao", ScientificName = "Theobroma cacao", Category = CropCategory.Plantation,
            BaseTempC = 13, CutoffTempC = 32, OptimalTempMinC = 22, OptimalTempMaxC = 30, AbsoluteTempMinC = 15, AbsoluteTempMaxC = 35, GddToMaturity = 4500,
            RainfallMinMm = 1250, RainfallOptMinMm = 1500, RainfallOptMaxMm = 2500, RainfallMaxMm = 3500, PhMin = 4.5, PhOptMin = 5.5, PhOptMax = 7.0, PhMax = 8.0, ElevationMaxM = 800,
            KcIni = 1.00, KcMid = 1.05, KcEnd = 1.05, StageDays = [60, 90, 150, 65], YieldResponseFactorKy = 1.0, MaxRootDepthM = 1.2, DepletionFraction = 0.3,
            PotentialYieldTonHa = 2.0, HarvestIndex = 0.2, RueGPerMj = 1.0, Removal = new(40, 18, 60), Stages = GenericStages },
        new() { Id = "peanut", Name = "Peanut", NameId = "Kacang tanah", ScientificName = "Arachis hypogaea", Category = CropCategory.Legume,
            BaseTempC = 10, CutoffTempC = 33, OptimalTempMinC = 22, OptimalTempMaxC = 30, GddToMaturity = 1700,
            RainfallMinMm = 400, RainfallOptMinMm = 500, RainfallOptMaxMm = 1000, RainfallMaxMm = 1800, PhMin = 5.0, PhOptMin = 5.8, PhOptMax = 6.8, PhMax = 7.5, ElevationMaxM = 1000,
            KcIni = 0.40, KcMid = 1.15, KcEnd = 0.60, StageDays = [25, 35, 45, 25], YieldResponseFactorKy = 0.7, MaxRootDepthM = 0.8, DepletionFraction = 0.5,
            PotentialYieldTonHa = 4, HarvestIndex = 0.4, RueGPerMj = 1.6, Removal = new(50, 10, 15), Stages = GenericStages },
        new() { Id = "mungbean", Name = "Mung bean", NameId = "Kacang hijau", ScientificName = "Vigna radiata", Category = CropCategory.Legume,
            BaseTempC = 10, CutoffTempC = 35, OptimalTempMinC = 25, OptimalTempMaxC = 32, GddToMaturity = 1100,
            RainfallMinMm = 300, RainfallOptMinMm = 400, RainfallOptMaxMm = 800, RainfallMaxMm = 1500, PhMin = 5.0, PhOptMin = 6.0, PhOptMax = 7.0, PhMax = 8.0, ElevationMaxM = 800,
            KcIni = 0.40, KcMid = 1.05, KcEnd = 0.35, StageDays = [15, 20, 25, 10], YieldResponseFactorKy = 0.9, MaxRootDepthM = 0.8, DepletionFraction = 0.45,
            PotentialYieldTonHa = 2, HarvestIndex = 0.35, RueGPerMj = 1.5, Removal = new(45, 12, 18), Stages = GenericStages },
        new() { Id = "banana", Name = "Banana", NameId = "Pisang", ScientificName = "Musa spp.", Category = CropCategory.Fruit,
            BaseTempC = 14, CutoffTempC = 35, OptimalTempMinC = 24, OptimalTempMaxC = 32, AbsoluteTempMinC = 15, AbsoluteTempMaxC = 38, GddToMaturity = 4200,
            RainfallMinMm = 1000, RainfallOptMinMm = 1500, RainfallOptMaxMm = 2500, RainfallMaxMm = 4000, PhMin = 4.5, PhOptMin = 5.5, PhOptMax = 7.0, PhMax = 8.0, ElevationMaxM = 1200,
            KcIni = 0.50, KcMid = 1.10, KcEnd = 1.00, StageDays = [120, 90, 120, 60], YieldResponseFactorKy = 1.3, MaxRootDepthM = 0.8, DepletionFraction = 0.35,
            PotentialYieldTonHa = 40, HarvestIndex = 0.5, RueGPerMj = 1.2, Removal = new(2, 0.5, 6), Stages = GenericStages },
    ];
}
