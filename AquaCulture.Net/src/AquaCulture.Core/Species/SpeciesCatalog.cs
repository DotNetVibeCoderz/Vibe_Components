namespace AquaCulture.Core.Species;

/// <summary>
/// Built-in species database: nila, lele, gurame, patin, koi, bandeng, kerapu, kakap, udang vaname and udang windu.
/// Look up by id (<c>"nila"</c>), English/Indonesian name or scientific name. Register your own with <see cref="Register"/>.
/// </summary>
public static class SpeciesCatalog
{
    private static readonly Dictionary<string, SpeciesProfile> ById = new(StringComparer.OrdinalIgnoreCase);

    public static readonly SpeciesProfile Nila = new()
    {
        Id = "nila", Name = new("Nile tilapia", "Ikan nila"), ScientificName = "Oreochromis niloticus",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Euryhaline,
        Temperature = new(18, 26, 32, 38), Ph = new(5.5, 6.5, 8.5, 10), Salinity = new(0, 0, 15, 30), Alkalinity = new(20, 50, 150, 300),
        DoCritical = 2.0, DoOptimal = 5.0, Nh3Max = 0.05, TanMax = 2.0, NitriteMax = 0.5,
        Tgc = 1.6, LInfinityCm = 45, VbK = 0.6, VbT0 = -0.15, LwA = 0.0178, LwB = 3.0,
        TypicalFcr = 1.5, StockingWeightG = 5, MarketWeightG = 400, TypicalCycleDays = 150, TypicalDensity = 5, MaxBiomassKgM3 = 3.5,
        TypicalSurvival = 0.85, PricePerKg = 30_000, ProteinRequirementPct = 30,
        FeedingTable = [(1, 10), (5, 6), (20, 4), (50, 3.2), (100, 2.6), (200, 2.0), (400, 1.5), (700, 1.2)],
        Tags = ["freshwater", "pond", "cage", "biofloc", "aquaponic"],
    };

    public static readonly SpeciesProfile Lele = new()
    {
        Id = "lele", Name = new("African catfish", "Ikan lele"), ScientificName = "Clarias gariepinus",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Freshwater,
        Temperature = new(20, 25, 30, 35), Ph = new(5.5, 6.5, 8.5, 9.5), Salinity = new(0, 0, 3, 8), Alkalinity = new(20, 50, 200, 300),
        DoCritical = 1.0, DoOptimal = 3.0, Nh3Max = 0.1, TanMax = 4.0, NitriteMax = 1.0,
        Tgc = 2.0, LInfinityCm = 100, VbK = 0.4, VbT0 = -0.1, LwA = 0.0085, LwB = 3.0,
        TypicalFcr = 1.0, StockingWeightG = 5, MarketWeightG = 125, TypicalCycleDays = 70, TypicalDensity = 200, MaxBiomassKgM3 = 40,
        TypicalSurvival = 0.80, PricePerKg = 22_000, ProteinRequirementPct = 32,
        FeedingTable = [(1, 10), (5, 7), (20, 5), (50, 4), (100, 3), (150, 2.5), (300, 2)],
        Tags = ["freshwater", "tarpaulin", "biofloc", "ras"],
    };

    public static readonly SpeciesProfile Gurame = new()
    {
        Id = "gurame", Name = new("Giant gourami", "Ikan gurame"), ScientificName = "Osphronemus goramy",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Freshwater,
        Temperature = new(20, 25, 30, 35), Ph = new(5.0, 6.5, 8.0, 9.0), Salinity = new(0, 0, 2, 5), Alkalinity = new(20, 40, 150, 250),
        DoCritical = 1.5, DoOptimal = 4.0, Nh3Max = 0.05, TanMax = 1.5, NitriteMax = 0.5,
        Tgc = 0.65, LInfinityCm = 60, VbK = 0.25, VbT0 = -0.2, LwA = 0.02, LwB = 3.0,
        TypicalFcr = 1.8, StockingWeightG = 50, MarketWeightG = 500, TypicalCycleDays = 240, TypicalDensity = 10, MaxBiomassKgM3 = 3,
        TypicalSurvival = 0.85, PricePerKg = 45_000, ProteinRequirementPct = 28,
        FeedingTable = [(5, 5), (20, 4), (50, 3), (150, 2.5), (300, 2), (500, 1.5), (900, 1.2)],
        Tags = ["freshwater", "pond"],
    };

    public static readonly SpeciesProfile Patin = new()
    {
        Id = "patin", Name = new("Striped catfish (pangasius)", "Ikan patin"), ScientificName = "Pangasianodon hypophthalmus",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Freshwater,
        Temperature = new(22, 26, 32, 36), Ph = new(5.5, 6.5, 8.5, 9.5), Salinity = new(0, 0, 4, 10), Alkalinity = new(20, 40, 200, 300),
        DoCritical = 1.0, DoOptimal = 3.0, Nh3Max = 0.1, TanMax = 3.0, NitriteMax = 1.0,
        Tgc = 1.5, LInfinityCm = 110, VbK = 0.35, VbT0 = -0.1, LwA = 0.01, LwB = 3.0,
        TypicalFcr = 1.6, StockingWeightG = 10, MarketWeightG = 1000, TypicalCycleDays = 180, TypicalDensity = 25, MaxBiomassKgM3 = 15,
        TypicalSurvival = 0.80, PricePerKg = 20_000, ProteinRequirementPct = 28,
        FeedingTable = [(2, 8), (10, 5), (50, 3.5), (150, 2.8), (400, 2.0), (800, 1.5), (1200, 1.2)],
        Tags = ["freshwater", "pond", "cage"],
    };

    public static readonly SpeciesProfile Koi = new()
    {
        Id = "koi", Name = new("Koi carp", "Ikan koi"), ScientificName = "Cyprinus rubrofuscus",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Freshwater,
        Temperature = new(4, 20, 27, 33), Ph = new(6.0, 7.0, 8.5, 9.5), Salinity = new(0, 0, 3, 8), Alkalinity = new(40, 80, 200, 300),
        DoCritical = 3.0, DoOptimal = 6.0, Nh3Max = 0.02, TanMax = 0.5, NitriteMax = 0.2,
        Tgc = 1.0, LInfinityCm = 80, VbK = 0.2, VbT0 = -0.3, LwA = 0.02, LwB = 3.0,
        TypicalFcr = 1.6, StockingWeightG = 5, MarketWeightG = 250, TypicalCycleDays = 180, TypicalDensity = 3, MaxBiomassKgM3 = 1.5,
        TypicalSurvival = 0.90, PricePerKg = 150_000, ProteinRequirementPct = 35,
        FeedingTable = [(1, 6), (5, 4), (20, 3), (100, 2), (300, 1.5), (1000, 1)],
        Tags = ["ornamental", "freshwater", "ras"],
    };

    public static readonly SpeciesProfile Bandeng = new()
    {
        Id = "bandeng", Name = new("Milkfish", "Ikan bandeng"), ScientificName = "Chanos chanos",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Euryhaline,
        Temperature = new(18, 26, 32, 38), Ph = new(6.5, 7.5, 8.5, 9.5), Salinity = new(0, 10, 30, 45), Alkalinity = new(50, 80, 200, 300),
        DoCritical = 2.0, DoOptimal = 4.0, Nh3Max = 0.05, TanMax = 1.5, NitriteMax = 0.5,
        Tgc = 1.3, LInfinityCm = 120, VbK = 0.4, VbT0 = -0.1, LwA = 0.011, LwB = 3.0,
        TypicalFcr = 1.7, StockingWeightG = 5, MarketWeightG = 350, TypicalCycleDays = 140, TypicalDensity = 3, MaxBiomassKgM3 = 2,
        TypicalSurvival = 0.85, PricePerKg = 32_000, ProteinRequirementPct = 26,
        FeedingTable = [(1, 8), (5, 5), (30, 3.5), (100, 2.5), (250, 2), (500, 1.5)],
        Tags = ["brackish", "tambak", "polyculture"],
    };

    public static readonly SpeciesProfile Kerapu = new()
    {
        Id = "kerapu", Name = new("Tiger grouper", "Ikan kerapu macan"), ScientificName = "Epinephelus fuscoguttatus",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Marine,
        Temperature = new(22, 27, 32, 35), Ph = new(7.0, 7.8, 8.5, 9.0), Salinity = new(15, 28, 34, 40), Alkalinity = new(80, 100, 200, 300),
        DoCritical = 3.0, DoOptimal = 5.0, Nh3Max = 0.02, TanMax = 0.5, NitriteMax = 0.3,
        Tgc = 0.85, LInfinityCm = 90, VbK = 0.2, VbT0 = -0.2, LwA = 0.016, LwB = 3.0,
        TypicalFcr = 1.6, StockingWeightG = 15, MarketWeightG = 600, TypicalCycleDays = 240, TypicalDensity = 15, MaxBiomassKgM3 = 15,
        TypicalSurvival = 0.75, PricePerKg = 120_000, ProteinRequirementPct = 48,
        FeedingTable = [(5, 6), (20, 4), (80, 3), (200, 2), (500, 1.5), (1000, 1.2)],
        Tags = ["marine", "kja", "cage", "live-fish"],
    };

    public static readonly SpeciesProfile Kakap = new()
    {
        Id = "kakap", Name = new("Barramundi (Asian seabass)", "Ikan kakap putih"), ScientificName = "Lates calcarifer",
        Group = SpeciesGroup.Fish, Habitat = Habitat.Euryhaline,
        Temperature = new(20, 26, 32, 36), Ph = new(6.5, 7.5, 8.5, 9.0), Salinity = new(0, 10, 32, 40), Alkalinity = new(50, 100, 200, 300),
        DoCritical = 3.0, DoOptimal = 5.0, Nh3Max = 0.03, TanMax = 1.0, NitriteMax = 0.5,
        Tgc = 1.2, LInfinityCm = 150, VbK = 0.3, VbT0 = -0.1, LwA = 0.012, LwB = 3.0,
        TypicalFcr = 1.4, StockingWeightG = 10, MarketWeightG = 600, TypicalCycleDays = 180, TypicalDensity = 20, MaxBiomassKgM3 = 20,
        TypicalSurvival = 0.80, PricePerKg = 75_000, ProteinRequirementPct = 45,
        FeedingTable = [(2, 8), (10, 5), (50, 3.5), (150, 2.5), (400, 1.8), (800, 1.3)],
        Tags = ["marine", "brackish", "kja", "cage", "ras"],
    };

    public static readonly SpeciesProfile Vaname = new()
    {
        Id = "vaname", Name = new("Whiteleg shrimp", "Udang vaname"), ScientificName = "Litopenaeus vannamei",
        Group = SpeciesGroup.Shrimp, Habitat = Habitat.Euryhaline,
        Temperature = new(20, 28, 32, 35), Ph = new(7.0, 7.5, 8.5, 9.0), Salinity = new(2, 15, 30, 45), Alkalinity = new(80, 120, 200, 300),
        DoCritical = 3.0, DoOptimal = 5.0, Nh3Max = 0.1, TanMax = 1.0, NitriteMax = 1.0, TurbidityMax = 80,
        Tgc = 0.85, LInfinityCm = 23, VbK = 2.0, VbT0 = 0, LwA = 0.0065, LwB = 3.1,
        TypicalFcr = 1.3, StockingWeightG = 0.002, MarketWeightG = 20, TypicalCycleDays = 110, TypicalDensity = 120, MaxBiomassKgM3 = 2.5,
        TypicalSurvival = 0.80, PricePerKg = 70_000, ProteinRequirementPct = 35,
        FeedingTable = [(0.01, 30), (0.1, 15), (1, 8), (3, 5), (5, 4), (10, 3), (15, 2.5), (20, 2.2), (30, 1.8)],
        Tags = ["brackish", "tambak", "intensive", "lined-pond"],
    };

    public static readonly SpeciesProfile Windu = new()
    {
        Id = "windu", Name = new("Giant tiger prawn", "Udang windu"), ScientificName = "Penaeus monodon",
        Group = SpeciesGroup.Shrimp, Habitat = Habitat.Brackish,
        Temperature = new(20, 28, 32, 35), Ph = new(7.0, 7.5, 8.5, 9.0), Salinity = new(5, 15, 25, 40), Alkalinity = new(80, 100, 200, 300),
        DoCritical = 3.0, DoOptimal = 5.0, Nh3Max = 0.1, TanMax = 1.0, NitriteMax = 1.0, TurbidityMax = 80,
        Tgc = 0.73, LInfinityCm = 30, VbK = 1.5, VbT0 = 0, LwA = 0.0065, LwB = 3.1,
        TypicalFcr = 1.6, StockingWeightG = 0.002, MarketWeightG = 30, TypicalCycleDays = 140, TypicalDensity = 25, MaxBiomassKgM3 = 1.0,
        TypicalSurvival = 0.70, PricePerKg = 110_000, ProteinRequirementPct = 40,
        FeedingTable = [(0.01, 25), (0.1, 12), (1, 7), (5, 4.5), (10, 3.5), (20, 2.5), (30, 2), (40, 1.8)],
        Tags = ["brackish", "tambak", "extensive", "traditional"],
    };

    static SpeciesCatalog()
    {
        foreach (var s in new[] { Nila, Lele, Gurame, Patin, Koi, Bandeng, Kerapu, Kakap, Vaname, Windu }) ById[s.Id] = s;
    }

    /// <summary>All registered species.</summary>
    public static IReadOnlyCollection<SpeciesProfile> All => ById.Values;

    /// <summary>Finds a species by id, English/Indonesian name or scientific name.</summary>
    public static SpeciesProfile Get(string idOrName) =>
        TryGet(idOrName, out var s) ? s! : throw new KeyNotFoundException($"Unknown species '{idOrName}'. Known: {string.Join(", ", ById.Keys)}");

    public static bool TryGet(string idOrName, out SpeciesProfile? species)
    {
        if (ById.TryGetValue(idOrName.Trim(), out species)) return true;
        string q = idOrName.Trim().ToLowerInvariant();
        species = ById.Values.FirstOrDefault(s =>
            s.Name.En.Equals(q, StringComparison.OrdinalIgnoreCase) || s.Name.Id.Equals(q, StringComparison.OrdinalIgnoreCase) ||
            s.ScientificName.Equals(q, StringComparison.OrdinalIgnoreCase) || s.Name.Id.ToLowerInvariant().Replace("ikan ", "").Replace("udang ", "") == q ||
            q.Contains(s.Id));
        return species is not null;
    }

    /// <summary>Adds or replaces a species profile.</summary>
    public static void Register(SpeciesProfile species) => ById[species.Id] = species;
}
