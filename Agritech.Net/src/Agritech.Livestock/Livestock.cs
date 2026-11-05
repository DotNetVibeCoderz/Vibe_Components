using Agritech.Core.Data;
using Agritech.Core.Hosting;

namespace Agritech.Livestock;

public enum Species { BeefCattle, DairyCattle, Buffalo, Goat, Sheep, Pig, Broiler, Layer }
public enum Sex { Female, Male }

/// <summary>Species reference physiology.</summary>
public sealed record SpeciesProfile(Species Species, string NameId, int GestationDays, int EstrusCycleDays, (double Min, double Max) RectalTempC,
    (int Min, int Max) HeartRateBpm, (int Min, int Max) RespirationBpm, double DmiPctBodyWeight)
{
    public static SpeciesProfile For(Species s) => s switch
    {
        Species.BeefCattle => new(s, "Sapi potong", 283, 21, (38.0, 39.3), (40, 80), (10, 30), 2.5),
        Species.DairyCattle => new(s, "Sapi perah", 280, 21, (38.0, 39.3), (48, 84), (26, 50), 3.2),
        Species.Buffalo => new(s, "Kerbau", 310, 21, (37.5, 39.0), (40, 60), (18, 30), 2.3),
        Species.Goat => new(s, "Kambing", 150, 21, (38.5, 40.0), (70, 90), (12, 25), 3.5),
        Species.Sheep => new(s, "Domba", 147, 17, (38.5, 40.0), (60, 90), (12, 25), 3.0),
        Species.Pig => new(s, "Babi", 114, 21, (38.0, 39.5), (70, 120), (13, 18), 3.0),
        Species.Broiler => new(s, "Ayam broiler", 0, 0, (40.6, 41.7), (250, 300), (15, 36), 8.0),
        _ => new(s, "Ayam petelur", 0, 0, (40.6, 41.7), (250, 300), (15, 36), 6.5),
    };
}

public sealed record WeightRecord(DateOnly Date, double WeightKg);
public sealed record HealthEvent(DateOnly Date, string Type, string Description, string? Treatment = null, int WithdrawalDays = 0);
public sealed record BreedingEvent(DateOnly Date, string Type, string? SireId = null, bool? Pregnant = null);

/// <summary>An individually identified animal (ear tag / RFID).</summary>
public sealed class Animal(string tagId, Species species, Sex sex, DateOnly birthDate, string? breed = null)
{
    public string TagId { get; } = tagId;
    public Species Species { get; } = species;
    public Sex Sex { get; } = sex;
    public DateOnly BirthDate { get; } = birthDate;
    public string? Breed { get; } = breed;
    public string? DamId { get; init; }
    public string? SireId { get; init; }
    public List<WeightRecord> Weights { get; } = [];
    public List<HealthEvent> Health { get; } = [];
    public List<BreedingEvent> Breeding { get; } = [];
    public SpeciesProfile Profile => SpeciesProfile.For(Species);

    public int AgeDays(DateOnly today) => today.DayNumber - BirthDate.DayNumber;
    public double? CurrentWeight => Weights.Count == 0 ? null : Weights.MaxBy(w => w.Date)!.WeightKg;

    /// <summary>Average daily gain (kg/day) by linear regression over weight records.</summary>
    public double AverageDailyGain()
    {
        if (Weights.Count < 2) return 0;
        var x = Weights.Select(w => (double)w.Date.DayNumber).ToArray();
        var y = Weights.Select(w => w.WeightKg).ToArray();
        return Math.Round(Stats.LinearFit(x, y).Slope, 3);
    }

    /// <summary>True while a drug withdrawal period is active (meat/milk must not be sold).</summary>
    public bool InWithdrawal(DateOnly today) => Health.Any(h => h.WithdrawalDays > 0 && today.DayNumber <= h.Date.DayNumber + h.WithdrawalDays);
}

public enum HeatStressLevel { None, Mild, Moderate, Severe, Emergency }

/// <summary>Livestock welfare and productivity calculations.</summary>
public static class LivestockMath
{
    /// <summary>Temperature–Humidity Index (NRC 1971).</summary>
    public static double Thi(double tempC, double rhPct) => 1.8 * tempC + 32 - (0.55 - 0.0055 * rhPct) * (1.8 * tempC - 26);

    public static HeatStressLevel HeatStress(double thi) => thi < 72 ? HeatStressLevel.None : thi < 79 ? HeatStressLevel.Mild
        : thi < 89 ? HeatStressLevel.Moderate : thi < 99 ? HeatStressLevel.Severe : HeatStressLevel.Emergency;

    /// <summary>Estimated milk yield loss (kg/cow/day) — ≈0.2 kg per THI unit above 72 (Ravagnolo et al. 2000).</summary>
    public static double MilkLossKg(double thi) => Math.Max(0, (thi - 72) * 0.2);

    /// <summary>Schoorl formula widely used in Indonesia: W(kg) = (heart girth cm + 22)² / 100.</summary>
    public static double WeightFromHeartGirth(double heartGirthCm) => Math.Round(Math.Pow(heartGirthCm + 22, 2) / 100, 1);

    /// <summary>Daily dry-matter intake (kg) from body weight.</summary>
    public static double DryMatterIntake(Species s, double weightKg) => Math.Round(weightKg * SpeciesProfile.For(s).DmiPctBodyWeight / 100, 2);

    /// <summary>Estrus detection from pedometer activity: today's steps ≥ ratio × trailing 7-day mean.</summary>
    public static bool IsEstrusByActivity(IReadOnlyList<double> dailySteps, double ratio = 1.8)
    {
        if (dailySteps.Count < 8) return false;
        double baseline = dailySteps.Skip(dailySteps.Count - 8).Take(7).Average();
        return dailySteps[^1] >= ratio * baseline;
    }
}

public sealed record VitalSigns(string TagId, DateTimeOffset Time, double? RectalTempC = null, int? HeartRate = null, int? Respiration = null, double? Rumination = null);

public sealed record HealthAlert(string TagId, string Message, string Severity, DateTimeOffset Time);

/// <summary>Herd registry with health monitoring and reproduction calendar.</summary>
public sealed class Herd
{
    private readonly Dictionary<string, Animal> _animals = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Animal> Animals => _animals.Values;
    public Animal this[string tag] => _animals[tag];

    public Animal Add(Animal a) { _animals[a.TagId] = a; return a; }

    public IEnumerable<HealthAlert> CheckVitals(VitalSigns v)
    {
        if (!_animals.TryGetValue(v.TagId, out var a)) yield break;
        var p = a.Profile;
        if (v.RectalTempC is { } t)
        {
            if (t > p.RectalTempC.Max + 0.5) yield return new(v.TagId, $"Fever {t:F1} °C (normal {p.RectalTempC.Min}-{p.RectalTempC.Max})", "high", v.Time);
            else if (t < p.RectalTempC.Min - 0.5) yield return new(v.TagId, $"Hypothermia {t:F1} °C", "high", v.Time);
        }
        if (v.HeartRate is { } hr && (hr < p.HeartRateBpm.Min || hr > p.HeartRateBpm.Max * 1.15))
            yield return new(v.TagId, $"Abnormal heart rate {hr} bpm", "medium", v.Time);
        if (v.Respiration is { } rr && rr > p.RespirationBpm.Max * 1.5)
            yield return new(v.TagId, $"Rapid breathing {rr}/min — check heat stress or respiratory disease", "medium", v.Time);
        if (v.Rumination is { } rum && a.Species is Species.DairyCattle or Species.BeefCattle && rum < 300)
            yield return new(v.TagId, $"Low rumination {rum:F0} min/day — possible digestive/metabolic disorder", "medium", v.Time);
    }

    /// <summary>Reproduction calendar: expected next heat, pregnancy check and due date for each breeding female.</summary>
    public IReadOnlyList<(string TagId, string Event, DateOnly Date)> ReproductionCalendar(DateOnly today)
    {
        var res = new List<(string, string, DateOnly)>();
        foreach (var a in _animals.Values.Where(x => x.Sex == Sex.Female && x.Profile.GestationDays > 0))
        {
            var lastService = a.Breeding.Where(b => b.Type is "AI" or "Natural").MaxBy(b => b.Date);
            if (lastService is null) continue;
            var preg = a.Breeding.Where(b => b.Pregnant is not null && b.Date >= lastService.Date).MaxBy(b => b.Date);
            if (preg?.Pregnant == true)
            {
                var due = lastService.Date.AddDays(a.Profile.GestationDays);
                res.Add((a.TagId, "Expected parturition / Perkiraan melahirkan", due));
                res.Add((a.TagId, "Dry-off / Kering kandang", due.AddDays(-60)));
            }
            else if (preg?.Pregnant == false || today.DayNumber - lastService.Date.DayNumber < 35)
            {
                int cycle = a.Profile.EstrusCycleDays;
                var next = lastService.Date.AddDays(cycle);
                while (next < today) next = next.AddDays(cycle);
                res.Add((a.TagId, "Watch for heat / Amati birahi", next));
                if (preg is null) res.Add((a.TagId, "Pregnancy check / Pemeriksaan kebuntingan", lastService.Date.AddDays(35)));
            }
        }
        return res.OrderBy(r => r.Item3).ToList();
    }
}

public static class LivestockBuilderExtensions
{
    public static AgritechBuilder AddLivestock(this AgritechBuilder builder)
    {
        builder.TryAddModule("Livestock");
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<Herd>(builder.Services);
        return builder;
    }
}
