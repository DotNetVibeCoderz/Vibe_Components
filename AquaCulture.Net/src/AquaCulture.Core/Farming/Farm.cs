using AquaCulture.Core.Geo;
using AquaCulture.Core.Species;

namespace AquaCulture.Core.Farming;

/// <summary>Production system of a culture unit.</summary>
public enum CultureSystem
{
    /// <summary>Traditional earthen pond (kolam tanah / tambak tradisional).</summary>
    EarthenPond,
    /// <summary>Concrete pond (kolam beton).</summary>
    ConcretePond,
    /// <summary>HDPE / plastic-lined pond (tambak plastik, kolam terpal).</summary>
    LinedPond,
    /// <summary>Biofloc tank or pond.</summary>
    Biofloc,
    /// <summary>Floating net cage (KJA, keramba jaring apung) in a lake, reservoir or the sea.</summary>
    FloatingNetCage,
    /// <summary>Recirculating aquaculture system.</summary>
    Ras,
    /// <summary>Hatchery / nursery tank.</summary>
    Hatchery,
    /// <summary>Aquaponic fish tank coupled to a plant bed.</summary>
    Aquaponic,
    /// <summary>Flow-through raceway (kolam air deras).</summary>
    Raceway,
}

/// <summary>Kind of operation.</summary>
public enum FarmType { FishFarm, ShrimpFarm, Hatchery, CageCulture, IndoorRas, Aquaponic, Research, Government }

/// <summary>A culture unit (pond, tank or cage).</summary>
public sealed class Pond
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public CultureSystem System { get; init; } = CultureSystem.EarthenPond;
    /// <summary>Water surface area (m²).</summary>
    public double AreaM2 { get; init; }
    /// <summary>Mean water depth (m).</summary>
    public double DepthM { get; init; } = 1.2;
    /// <summary>Installed aeration power (kW, paddle wheels / blowers).</summary>
    public double AerationKw { get; set; }
    /// <summary>Daily water exchange (% of volume).</summary>
    public double WaterExchangePctPerDay { get; set; }
    public GeoPolygon? Outline { get; init; }
    public GeoCoordinate? Location { get; init; }
    public double VolumeM3 => AreaM2 * DepthM;
    public double AreaHa => AreaM2 / 10_000;

    public override string ToString() => $"{Name} ({Id}, {System}, {AreaM2:0} m²)";
}

/// <summary>A farm with its culture units and culture cycles.</summary>
public sealed class Farm
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public FarmType Type { get; init; } = FarmType.FishFarm;
    public GeoCoordinate Location { get; init; }
    public double ElevationM { get; init; }
    public string Owner { get; init; } = "";
    public List<Pond> Ponds { get; } = [];
    public List<CultureCycle> Cycles { get; } = [];

    public Pond Pond(string id) => Ponds.First(p => p.Id == id);
    public IEnumerable<CultureCycle> ActiveCycles => Cycles.Where(c => c.IsActive);
    public CultureCycle? ActiveCycle(string pondId) => Cycles.LastOrDefault(c => c.PondId == pondId && c.IsActive);
    public double TotalWaterAreaM2 => Ponds.Sum(p => p.AreaM2);
}

public sealed record SamplingRecord(DateOnly Date, double AverageWeightG, int SampleSize, double? AverageLengthCm = null, double? WeightStdDevG = null);

public sealed record FeedRecord(DateOnly Date, double Kg, string FeedId = "", double PricePerKg = 0);

public sealed record MortalityRecord(DateOnly Date, long Count, string Cause = "");

public sealed record HarvestRecord(DateOnly Date, double Kg, long Count, double PricePerKg, bool Partial = false);

public sealed record ExpenseRecord(DateOnly Date, string Category, double Amount, string Note = "");

/// <summary>
/// A production cycle (siklus) of one species in one unit, from stocking (tebar) to harvest (panen),
/// with sampling, feed, mortality, harvest and expense logs.
/// </summary>
public sealed class CultureCycle
{
    public required string Id { get; init; }
    public required string PondId { get; init; }
    public required SpeciesProfile Species { get; init; }
    public DateOnly StockingDate { get; init; }
    public long StockedCount { get; init; }
    public double StockingWeightG { get; init; }
    public double SeedPricePerUnit { get; init; }
    public DateOnly? ClosedDate { get; set; }

    public List<SamplingRecord> Samplings { get; } = [];
    public List<FeedRecord> Feedings { get; } = [];
    public List<MortalityRecord> Mortalities { get; } = [];
    public List<HarvestRecord> Harvests { get; } = [];
    public List<ExpenseRecord> Expenses { get; } = [];

    public bool IsActive => ClosedDate is null;

    /// <summary>Day of culture (DOC) on a date; the stocking day is DOC 0.</summary>
    public int DayOfCulture(DateOnly date) => date.DayNumber - StockingDate.DayNumber;

    public long RecordedDeaths => Mortalities.Sum(m => m.Count);
    public long HarvestedCount => Harvests.Sum(h => h.Count);
    public double HarvestedKg => Harvests.Sum(h => h.Kg);
    public double TotalFeedKg => Feedings.Sum(f => f.Kg);

    /// <summary>Animals believed alive (stocked − recorded deaths − harvested). Unrecorded losses are estimated by sampling elsewhere.</summary>
    public long EstimatedPopulation => Math.Max(0, StockedCount - RecordedDeaths - HarvestedCount);

    /// <summary>Latest sampled average body weight (ABW), or the stocking weight.</summary>
    public double LatestAbwG => Samplings.Count == 0 ? StockingWeightG : Samplings.MaxBy(s => s.Date)!.AverageWeightG;

    public DateOnly LatestSamplingDate => Samplings.Count == 0 ? StockingDate : Samplings.Max(s => s.Date);

    /// <summary>Standing biomass (kg) from the estimated population and latest ABW.</summary>
    public double EstimatedBiomassKg => EstimatedPopulation * LatestAbwG / 1000;

    public double InitialBiomassKg => StockedCount * StockingWeightG / 1000;

    public CultureCycle LogSampling(DateOnly date, double abwG, int sampleSize = 30, double? lengthCm = null) { Samplings.Add(new(date, abwG, sampleSize, lengthCm)); return this; }
    public CultureCycle LogFeed(DateOnly date, double kg, string feedId = "", double pricePerKg = 0) { Feedings.Add(new(date, kg, feedId, pricePerKg)); return this; }
    public CultureCycle LogMortality(DateOnly date, long count, string cause = "") { Mortalities.Add(new(date, count, cause)); return this; }
    public CultureCycle LogHarvest(DateOnly date, double kg, long count, double pricePerKg, bool partial = false)
    {
        Harvests.Add(new(date, kg, count, pricePerKg, partial));
        if (!partial) ClosedDate = date;
        return this;
    }
    public CultureCycle LogExpense(DateOnly date, string category, double amount, string note = "") { Expenses.Add(new(date, category, amount, note)); return this; }
}
