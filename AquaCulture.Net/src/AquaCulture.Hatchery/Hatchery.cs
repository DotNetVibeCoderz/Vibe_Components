using System.Globalization;
using AquaCulture.Core;
using AquaCulture.Core.Hosting;
using AquaCulture.Traceability;

namespace AquaCulture.Hatchery;

public enum Sex { Female, Male }
public enum PcrResult { NotTested, Negative, Positive }

/// <summary>A broodstock animal (induk), with its origin line and pathogen screening.</summary>
public sealed record Broodstock(string Id, Sex Sex, string Origin, double WeightG, DateOnly Arrived)
{
    /// <summary>Specific-pathogen-free certified line.</summary>
    public bool Spf { get; init; } = true;
    public bool EyestalkAblated { get; init; }
    public IReadOnlyDictionary<string, PcrResult> Pcr { get; init; } = new Dictionary<string, PcrResult>();
    public bool Culled { get; init; }
}

/// <summary>A spawning (pemijahan) of one female.</summary>
public sealed record Spawning(DateOnly Date, string FemaleId, string? MaleId, long Eggs, long Nauplii)
{
    public double HatchingRate => Eggs > 0 ? (double)Nauplii / Eggs : 0;
}

/// <summary>Performance of a female over her spawnings.</summary>
public sealed record FemalePerformance(string FemaleId, int Spawns, double MeanNauplii, double MeanHatchingRate, int DaysSinceLast);

/// <summary>Broodstock registry, spawnings and maturation statistics.</summary>
public sealed class BroodstockUnit
{
    public static readonly string[] RequiredPathogens = ["WSSV", "IHHNV", "TSV", "YHV", "IMNV", "AHPND", "EHP"];

    public List<Broodstock> Animals { get; } = [];
    public List<Spawning> Spawnings { get; } = [];

    public IEnumerable<Broodstock> Active => Animals.Where(a => !a.Culled);

    /// <summary>Animals that may enter maturation: SPF and negative for every listed pathogen.</summary>
    public IEnumerable<Broodstock> Cleared => Active.Where(a => a.Spf && RequiredPathogens.All(p => a.Pcr.TryGetValue(p, out var r) && r == PcrResult.Negative));

    public IReadOnlyList<FemalePerformance> FemalePerformance(DateOnly today) =>
        Spawnings.GroupBy(s => s.FemaleId).Select(g => new FemalePerformance(g.Key, g.Count(), Math.Round(g.Average(s => s.Nauplii)),
            Math.Round(g.Average(s => s.HatchingRate), 3), today.DayNumber - g.Max(s => s.Date).DayNumber)).OrderByDescending(f => f.MeanNauplii).ToList();

    /// <summary>Share of active females that spawned each night (maturation performance; well-run units reach 10–20 %).</summary>
    public double SpawningRate(DateOnly from, DateOnly to)
    {
        int females = Active.Count(a => a.Sex == Sex.Female);
        int nights = to.DayNumber - from.DayNumber + 1;
        return females == 0 || nights <= 0 ? 0 : Spawnings.Count(s => s.Date >= from && s.Date <= to) / (double)(females * nights);
    }

    /// <summary>Females to replace: at least <paramref name="minSpawns"/> spawns and nauplii below <paramref name="fraction"/> of the unit mean, or idle for long.</summary>
    public IReadOnlyList<string> CullCandidates(DateOnly today, int minSpawns = 3, double fraction = 0.6, int idleDays = 21)
    {
        var perf = FemalePerformance(today);
        if (perf.Count == 0) return [];
        double mean = perf.Average(p => p.MeanNauplii);
        var spawned = perf.Select(p => p.FemaleId).ToHashSet();
        return perf.Where(p => p.Spawns >= minSpawns && p.MeanNauplii < fraction * mean || p.DaysSinceLast > idleDays).Select(p => p.FemaleId)
            .Concat(Active.Where(a => a.Sex == Sex.Female && !spawned.Contains(a.Id) && today.DayNumber - a.Arrived.DayNumber > idleDays).Select(a => a.Id))
            .Distinct().ToList();
    }
}

/// <summary>Penaeid larval stages from nauplius to post-larva.</summary>
public enum LarvalStage { N, Z1, Z2, Z3, M1, M2, M3, PL }

/// <summary>Daily feeding of a larval tank.</summary>
/// <param name="AlgaeCellsPerMl">Diatom density to maintain (Chaetoceros / Thalassiosira).</param>
/// <param name="ArtemiaPerLarva">Artemia nauplii per larva per day.</param>
/// <param name="MicrodietGPerMillion">Micro-diet per million larvae per day (g).</param>
public sealed record LarvalFeeding(LarvalStage Stage, double AlgaeCellsPerMl, double ArtemiaPerLarva, double MicrodietGPerMillion);

/// <summary>
/// Larval development and feeding of whiteleg shrimp. Stage durations at 29 °C (N ≈ 1.8 d; each zoea ≈ 1.2 d; each mysis ≈ 1 d) are
/// scaled with Q10 = 2.2, so PL1 comes about 8–9 days after nauplii at 29 °C. Feeding and stage survivals are indicative hatchery
/// practice; set your own protocol.
/// </summary>
public static class LarvalRearing
{
    private static readonly (LarvalStage Stage, double Days, double Survival)[] Stages =
    [
        (LarvalStage.N, 1.8, 0.95), (LarvalStage.Z1, 1.2, 0.92), (LarvalStage.Z2, 1.2, 0.93), (LarvalStage.Z3, 1.2, 0.94),
        (LarvalStage.M1, 1.0, 0.95), (LarvalStage.M2, 1.0, 0.96), (LarvalStage.M3, 1.0, 0.96),
    ];

    public static double TemperatureFactor(double tempC, double q10 = 2.2) => Math.Pow(q10, (tempC - 29) / 10);

    /// <summary>Stage on each day after stocking nauplii, until PL<paramref name="plDays"/>.</summary>
    public static IReadOnlyList<(int Day, LarvalStage Stage, int PlDay)> Schedule(double tempC, int plDays = 10)
    {
        double f = TemperatureFactor(tempC), t = 0;
        var bounds = Stages.Select(s => t += s.Days / f).ToArray();
        var list = new List<(int, LarvalStage, int)>();
        for (int d = 0; ; d++)
        {
            int idx = Array.FindIndex(bounds, b => d + 0.5 < b);
            if (idx >= 0) list.Add((d, Stages[idx].Stage, 0));
            else
            {
                int pl = (int)Math.Floor(d + 0.5 - bounds[^1]) + 1;
                if (pl > plDays) break;
                list.Add((d, LarvalStage.PL, pl));
            }
        }
        return list;
    }

    public static LarvalFeeding Feeding(LarvalStage stage, int plDay = 0) => stage switch
    {
        LarvalStage.N => new(stage, 0, 0, 0),                          // nauplii live on yolk
        LarvalStage.Z1 => new(stage, 80_000, 0, 4),
        LarvalStage.Z2 or LarvalStage.Z3 => new(stage, 110_000, 0, 6),
        LarvalStage.M1 => new(stage, 80_000, 5, 8),
        LarvalStage.M2 or LarvalStage.M3 => new(stage, 60_000, 15, 10),
        _ => new(stage, Math.Max(10_000, 40_000 - 4_000 * plDay), 30 + 5 * plDay, 12 + 2 * plDay),
    };

    /// <summary>Survival through the stages (nauplii → PL1) with an extra multiplier for the batch's conditions.</summary>
    public static double ExpectedSurvivalToPl1(double conditionFactor = 1) =>
        Stages.Aggregate(1.0, (s, x) => s * Math.Min(1, x.Survival * conditionFactor));
}

/// <summary>A larval rearing tank batch.</summary>
public sealed class LarvalBatch
{
    public required string Id { get; init; }
    public required string TankId { get; init; }
    public required DateOnly Stocked { get; init; }
    public required long NaupliiStocked { get; init; }
    public double TankVolumeM3 { get; init; } = 10;
    public double TemperatureC { get; init; } = 29;
    /// <summary>Spawnings the nauplii came from (genealogy for the seed lot).</summary>
    public List<Spawning> Sources { get; } = [];
    /// <summary>Population counts from volumetric sampling (date, estimate).</summary>
    public List<(DateOnly Date, long Count)> Counts { get; } = [];
    public long? HarvestedPl { get; set; }
    public DateOnly? HarvestDate { get; set; }

    public double DensityPerLitre => NaupliiStocked / (TankVolumeM3 * 1000);
    public long LatestCount => Counts.Count == 0 ? NaupliiStocked : Counts.MaxBy(c => c.Date).Count;
    public double Survival => (double)(HarvestedPl ?? LatestCount) / NaupliiStocked;

    public (LarvalStage Stage, int PlDay) StageOn(DateOnly date)
    {
        int d = date.DayNumber - Stocked.DayNumber;
        var s = LarvalRearing.Schedule(TemperatureC, 30);
        if (d < 0) return (LarvalStage.N, 0);
        int i = s.ToList().FindIndex(x => x.Day == d);           // (0, N, 0) equals default(tuple): search by index, not by value
        return i < 0 ? (LarvalStage.PL, s[^1].PlDay + d - s[^1].Day) : (s[i].Stage, s[i].PlDay);
    }

    /// <summary>Feed for a day: algae to dose (litres of a culture at <paramref name="cultureCellsPerMl"/>), Artemia (millions) and micro-diet (g).</summary>
    public (double AlgaeLitres, double ArtemiaMillions, double MicrodietG) DailyFeed(DateOnly date, double cultureCellsPerMl = 2_000_000, double residualCellsPerMl = 20_000)
    {
        var (stage, pl) = StageOn(date);
        var f = LarvalRearing.Feeding(stage, pl);
        double larvae = LatestCount;
        double algae = Math.Max(0, f.AlgaeCellsPerMl - residualCellsPerMl) * TankVolumeM3 * 1e6 / cultureCellsPerMl / 1000;
        return (Math.Round(algae, 1), Math.Round(f.ArtemiaPerLarva * larvae / 1e6, 2), Math.Round(f.MicrodietGPerMillion * larvae / 1e6, 1));
    }
}

/// <summary>A post-larva quality sample (the checks Indonesian shrimp farmers ask hatcheries for).</summary>
public sealed record PlSample
{
    public int PlDay { get; init; } = 10;
    /// <summary>Survival in a stress test (salinity drop or 100 ppm formalin for 30 min), 0–1.</summary>
    public double StressTestSurvival { get; init; }
    /// <summary>Coefficient of variation of total length.</summary>
    public double LengthCv { get; init; }
    /// <summary>Ratio of abdominal muscle to gut width at the 6th segment (≥ 4 is good).</summary>
    public double MuscleGutRatio { get; init; }
    public double GutFullness { get; init; } = 1;
    public double NecrosisShare { get; init; }
    public double FoulingShare { get; init; }
    public IReadOnlyDictionary<string, PcrResult> Pcr { get; init; } = new Dictionary<string, PcrResult>();
}

public sealed record PlQuality(double Score, bool Pass, IReadOnlyList<Text2> Issues);

/// <summary>Scores a PL sample 0–100; any positive PCR, a weak stress test or high necrosis fails the lot.</summary>
public static class PlQualityAssessment
{
    public static PlQuality Evaluate(PlSample s)
    {
        var issues = new List<Text2>();
        double score = 0;
        score += 30 * Math.Clamp((s.StressTestSurvival - 0.5) / 0.4, 0, 1);
        score += 15 * Math.Clamp((0.30 - s.LengthCv) / 0.15, 0, 1);
        score += 15 * Math.Clamp((s.MuscleGutRatio - 2) / 2, 0, 1);
        score += 10 * Math.Clamp(s.GutFullness, 0, 1);
        score += 15 * Math.Clamp(1 - s.NecrosisShare / 0.1, 0, 1);
        score += 5 * Math.Clamp(1 - s.FoulingShare / 0.2, 0, 1);
        score += 10 * Math.Clamp((s.PlDay - 6) / 4.0, 0, 1);
        bool pass = true;
        foreach (var (p, r) in s.Pcr.Where(kv => kv.Value == PcrResult.Positive))
        {
            pass = false; issues.Add(new($"PCR positive for {p}: do not stock.", $"PCR positif {p}: jangan ditebar."));
        }
        foreach (var p in BroodstockUnit.RequiredPathogens.Where(p => !s.Pcr.ContainsKey(p) || s.Pcr[p] == PcrResult.NotTested))
            issues.Add(new($"{p} not tested.", $"{p} belum diuji."));
        if (s.StressTestSurvival < 0.8) { pass = false; issues.Add(new($"Stress-test survival {s.StressTestSurvival:P0} (< 80 %).", $"Sintasan uji stres {s.StressTestSurvival:P0} (< 80 %).")); }
        if (s.NecrosisShare > 0.05) { pass = false; issues.Add(new($"Necrosis in {s.NecrosisShare:P0} of PL.", $"Nekrosis pada {s.NecrosisShare:P0} PL.")); }
        if (s.LengthCv > 0.15) issues.Add(new($"Uneven size (CV {s.LengthCv:P0}).", $"Ukuran tidak seragam (CV {s.LengthCv:P0})."));
        if (s.MuscleGutRatio < 4) issues.Add(new($"Muscle:gut ratio {s.MuscleGutRatio:0.0} (< 4).", $"Rasio otot:usus {s.MuscleGutRatio:0.0} (< 4)."));
        if (s.PlDay < 8) issues.Add(new($"PL{s.PlDay} is young for pond stocking (PL8–12).", $"PL{s.PlDay} masih muda untuk tebar kolam (PL8–12)."));
        return new PlQuality(Math.Round(score, 1), pass && score >= 60, issues);
    }
}

/// <summary>Seed lots out of the hatchery, ready for traceability.</summary>
public static class SeedLots
{
    public static string LotNumber(LarvalBatch b, DateOnly harvest) => $"{b.TankId}-{harvest:yyMMdd}";

    /// <summary>The EPCIS commissioning event of a PL harvest, with genealogy and screening as lot master data.</summary>
    public static EpcisEvent Commissioning(LarvalBatch b, BroodstockUnit brood, PlSample quality, string seedGtin, string hatcheryGln, DateOnly harvest)
    {
        if (b.HarvestedPl is not { } count) throw new InvalidOperationException("Record the harvested PL first.");
        var parents = b.Sources.SelectMany(s => new[] { s.FemaleId, s.MaleId }).OfType<string>().Distinct().ToList();
        var origins = brood.Animals.Where(a => parents.Contains(a.Id)).Select(a => a.Origin).Distinct();
        var q = PlQualityAssessment.Evaluate(quality);
        var ilmd = new Dictionary<string, string>
        {
            ["cbvmda:speciesForFisheryStatisticsPurposesName"] = "Litopenaeus vannamei",
            ["aqua:stage"] = $"PL{quality.PlDay}",
            ["aqua:broodstockOrigin"] = string.Join(";", origins),
            ["aqua:spawningDates"] = string.Join(";", b.Sources.Select(s => s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Distinct()),
            ["aqua:survivalNaupliiToPl"] = b.Survival.ToString("0.000", CultureInfo.InvariantCulture),
            ["aqua:qualityScore"] = q.Score.ToString("0.0", CultureInfo.InvariantCulture),
            ["aqua:stressTestSurvival"] = quality.StressTestSurvival.ToString("0.00", CultureInfo.InvariantCulture),
        };
        foreach (var (p, r) in quality.Pcr) ilmd[$"aqua:pcr{p}"] = r.ToString();
        var loc = Gs1.Location(hatcheryGln, b.TankId);
        return new EpcisEvent
        {
            Type = EpcisEventType.ObjectEvent, EventTime = new DateTimeOffset(harvest.ToDateTime(new TimeOnly(4, 0)), TimeSpan.FromHours(7)),
            Action = EpcisAction.ADD, BizStep = EpcisJson.BizStep("commissioning"), Disposition = EpcisJson.Disposition(q.Pass ? "active" : "non_sellable_other"),
            ReadPoint = loc, BizLocation = loc, Quantities = [new(Gs1.Lot(seedGtin, LotNumber(b, harvest)), count, "H87")], Ilmd = ilmd,
        };
    }
}

public static class HatcheryBuilderExtensions
{
    public static AquaBuilder AddHatchery(this AquaBuilder b) => b.TryAddModule("Hatchery", _ => { });
}
