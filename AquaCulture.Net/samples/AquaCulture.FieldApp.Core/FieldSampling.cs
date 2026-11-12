using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquaCulture.Core;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Feeding;
using AquaCulture.IoT;
using AquaCulture.WaterQuality;

namespace AquaCulture.FieldApp;

/// <summary>A pond the field team visits.</summary>
public sealed record FieldPond(string Id, string Name, string SpeciesId, double AreaM2, double DepthM, long Population, double AerationKw = 0,
    CultureSystem System = CultureSystem.EarthenPond)
{
    public SpeciesProfile Species => SpeciesCatalog.Get(SpeciesId);
    public double VolumeM3 => AreaM2 * DepthM;
}

/// <summary>One visit to a pond: water measured with a hand-held meter or test kit, a cast-net sampling and a mortality count.</summary>
public sealed record FieldSample
{
    public required string PondId { get; init; }
    public string Sampler { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.UtcNow;
    public double? TemperatureC { get; init; }
    public double? DissolvedOxygen { get; init; }
    public double? Ph { get; init; }
    public double? SalinityPpt { get; init; }
    /// <summary>Total ammonia nitrogen (mg/L).</summary>
    public double? Ammonia { get; init; }
    public double? Nitrite { get; init; }
    public double? Alkalinity { get; init; }
    /// <summary>Secchi-disk transparency (cm).</summary>
    public double? TransparencyCm { get; init; }
    /// <summary>Animals in the cast-net sample.</summary>
    public int? SampleCount { get; init; }
    /// <summary>Weight of the cast-net sample (kg).</summary>
    public double? SampleWeightKg { get; init; }
    public int? Dead { get; init; }
    public string Note { get; init; } = "";

    /// <summary>Average body weight from the sampling (g), when both count and weight are entered.</summary>
    public double? AbwG => SampleCount is > 0 && SampleWeightKg is > 0 ? SampleWeightKg.Value * 1000 / SampleCount.Value : null;

    public WaterSample ToWaterSample() => new()
    {
        Timestamp = Time, PondId = PondId, TemperatureC = TemperatureC, DissolvedOxygen = DissolvedOxygen, Ph = Ph, SalinityPpt = SalinityPpt,
        Ammonia = Ammonia, Nitrite = Nitrite, Alkalinity = Alkalinity,
    };

    /// <summary>The measurements as sensor readings (only the values entered), for the offline-first sync.</summary>
    public IReadOnlyList<SensorReading> ToReadings(string deviceId)
    {
        var list = new List<SensorReading>();
        void Add(SensorKind k, double? v) { if (v is { } x) list.Add(new SensorReading(deviceId, PondId, k, x, Time.ToUniversalTime(), SensorReading.DefaultUnit(k))); }
        Add(SensorKind.Temperature, TemperatureC);
        Add(SensorKind.DissolvedOxygen, DissolvedOxygen);
        Add(SensorKind.Ph, Ph);
        Add(SensorKind.Salinity, SalinityPpt);
        Add(SensorKind.Ammonia, Ammonia);
        Add(SensorKind.Nitrite, Nitrite);
        Add(SensorKind.Alkalinity, Alkalinity);
        Add(SensorKind.Transparency, TransparencyCm);
        Add(SensorKind.AverageWeight, AbwG);
        Add(SensorKind.Mortality, Dead);
        return list;
    }

    /// <summary>Typing mistakes worth a second look (value outside what a meter can show or a pond can have).</summary>
    public IReadOnlyList<Text2> Check()
    {
        var w = new List<Text2>();
        void Range(double? v, double lo, double hi, string en, string id)
        {
            if (v is { } x && (x < lo || x > hi))
                w.Add(new($"{en} {x.ToString("0.##", CultureInfo.InvariantCulture)} is outside {lo.ToString(CultureInfo.InvariantCulture)}–{hi.ToString(CultureInfo.InvariantCulture)}: check the entry.",
                          $"{id} {x.ToString("0.##", CultureInfo.InvariantCulture)} di luar {lo.ToString(CultureInfo.InvariantCulture)}–{hi.ToString(CultureInfo.InvariantCulture)}: periksa isian."));
        }
        Range(TemperatureC, 5, 40, "Temperature", "Suhu");
        Range(DissolvedOxygen, 0, 20, "DO", "DO");
        Range(Ph, 4, 11, "pH", "pH");
        Range(SalinityPpt, 0, 60, "Salinity", "Salinitas");
        Range(Ammonia, 0, 20, "TAN", "TAN");
        Range(Nitrite, 0, 20, "Nitrite", "Nitrit");
        Range(Alkalinity, 0, 500, "Alkalinity", "Alkalinitas");
        Range(TransparencyCm, 5, 300, "Transparency", "Kecerahan");
        Range(AbwG, 0.001, 5000, "Average weight (g)", "Berat rata-rata (g)");
        if ((SampleCount is null) != (SampleWeightKg is null))
            w.Add(new("Enter both the sample count and its weight to get the average weight.", "Isi jumlah ekor sampel dan beratnya untuk mendapat berat rata-rata."));
        if (Dead is < 0) w.Add(new("Dead count cannot be negative.", "Jumlah mati tidak boleh negatif."));
        return w;
    }
}

/// <summary>What the field app shows right after a sample is entered.</summary>
public sealed record FieldReport(
    WaterQualityAssessment Water, IReadOnlyList<Recommendation> Actions, double? AbwG, double? BiomassKg, FeedingPlan? Feed,
    IReadOnlyList<Text2> EntryWarnings, IReadOnlyList<Text2> Notes);

public static class FieldAnalysis
{
    /// <summary>
    /// Assesses the water for the pond's species, turns problems into dosed actions, and — when the sampling gives an average weight —
    /// estimates biomass and today's feeding plan (adjusted for the DO and NH₃ just measured).
    /// <paramref name="previous"/> (the last sample of the pond) adds growth since then.
    /// </summary>
    public static FieldReport Evaluate(FieldSample s, FieldPond pond, FieldSample? previous = null)
    {
        var species = pond.Species;
        var water = WaterQualityIndex.Assess(s.ToWaterSample(), species);
        double? abw = s.AbwG;
        double? biomass = abw is { } a ? a * pond.Population / 1000 : null;
        var actions = RecommendationEngine.Recommend(water, new PondContext(pond.VolumeM3, pond.AreaM2, biomass ?? 0, pond.AerationKw, pond.System));
        FeedingPlan? feed = abw is { } w && s.TemperatureC is { } t
            ? FeedingCalculator.Plan(species, w, pond.Population, t, s.DissolvedOxygen, water.UnionizedAmmonia)
            : null;

        var notes = new List<Text2>();
        if (abw is { } now && previous?.AbwG is { } before && s.Time > previous.Time)
        {
            double days = (s.Time - previous.Time).TotalDays;
            if (days >= 1)
            {
                double adg = (now - before) / days;
                notes.Add(new($"Growth since {previous.Time:yyyy-MM-dd}: {adg.ToString("0.00", CultureInfo.InvariantCulture)} g/day.",
                              $"Pertumbuhan sejak {previous.Time:yyyy-MM-dd}: {adg.ToString("0.00", CultureInfo.InvariantCulture)} g/hari."));
            }
        }
        if (s.TransparencyCm is { } secchi)
            notes.Add(secchi < 25 ? new("Water very green or turbid (Secchi < 25 cm): plankton bloom, watch dawn DO.", "Air sangat hijau/keruh (kecerahan < 25 cm): plankton blooming, waspadai DO subuh.")
                : secchi > 60 ? new("Water clear (Secchi > 60 cm): little plankton; consider fertilising.", "Air bening (kecerahan > 60 cm): plankton sedikit; pertimbangkan pemupukan.")
                : new("Transparency in the usual 25–60 cm range.", "Kecerahan dalam kisaran wajar 25–60 cm."));
        return new FieldReport(water, actions, abw, biomass, feed, s.Check(), notes);
    }
}

/// <summary>
/// The phone's offline journal: every saved sample goes into an <see cref="EdgeOutbox"/> as readings (synced to <c>aqua-cloud</c>
/// with <see cref="CloudSyncClient"/> when there is signal) and into a local history with notes; ponds are kept in <c>ponds.json</c>.
/// </summary>
public sealed class FieldJournal : IDisposable
{
    private readonly string _dir;
    private readonly object _gate = new();

    public FieldJournal(string directory, string deviceId)
    {
        _dir = Directory.CreateDirectory(directory).FullName;
        DeviceId = SyncWire.IsValidGatewayId(deviceId) ? deviceId : throw new ArgumentException("Device id must be 1–64 characters of [A-Za-z0-9._-].", nameof(deviceId));
        Outbox = new EdgeOutbox(Path.Combine(_dir, "outbox"));
    }

    public string DeviceId { get; }
    public EdgeOutbox Outbox { get; }
    private string HistoryPath => Path.Combine(_dir, "samples.jsonl");
    private string PondsPath => Path.Combine(_dir, "ponds.json");

    /// <summary>Saves a sample (readings to the outbox, the full sample to the history). Returns the readings queued.</summary>
    public int Save(FieldSample s)
    {
        var readings = s.ToReadings(DeviceId);
        lock (_gate)
        {
            if (readings.Count > 0) Outbox.Append(readings);
            File.AppendAllText(HistoryPath, ToJson(s) + "\n", Encoding.UTF8);
        }
        return readings.Count;
    }

    /// <summary>Saved samples, newest first, optionally for one pond.</summary>
    public IReadOnlyList<FieldSample> History(string? pondId = null, int max = 100)
    {
        lock (_gate)
        {
            if (!File.Exists(HistoryPath)) return [];
            return File.ReadAllLines(HistoryPath).Where(l => l.Length > 0).Select(FromJson).Where(x => x is not null).Select(x => x!)
                .Where(x => pondId is null || x.PondId == pondId).OrderByDescending(x => x.Time).Take(max).ToList();
        }
    }

    public IReadOnlyList<FieldPond> LoadPonds()
    {
        if (!File.Exists(PondsPath)) return [];
        var arr = JsonNode.Parse(File.ReadAllText(PondsPath)) as JsonArray ?? [];
        return arr.OfType<JsonObject>().Select(o => new FieldPond(
            (string)o["id"]!, (string?)o["name"] ?? (string)o["id"]!, (string?)o["species"] ?? "vaname", (double?)o["areaM2"] ?? 1000, (double?)o["depthM"] ?? 1.2,
            (long?)o["population"] ?? 0, (double?)o["aerationKw"] ?? 0,
            Enum.TryParse<CultureSystem>((string?)o["system"], out var cs) ? cs : CultureSystem.EarthenPond)).ToList();
    }

    public void SavePonds(IEnumerable<FieldPond> ponds)
    {
        var arr = new JsonArray(ponds.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["name"] = p.Name, ["species"] = p.SpeciesId, ["areaM2"] = p.AreaM2, ["depthM"] = p.DepthM, ["population"] = p.Population,
            ["aerationKw"] = p.AerationKw, ["system"] = p.System.ToString(),
        }).ToArray());
        File.WriteAllText(PondsPath, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>All samples as CSV (invariant culture), for sharing from the phone.</summary>
    public void ExportCsv(TextWriter w)
    {
        var ci = CultureInfo.InvariantCulture;
        static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string N(double? v) => v?.ToString("0.###", ci) ?? "";
        w.WriteLine("time_utc,pond,sampler,temp_c,do_mg_l,ph,salinity_ppt,tan_mg_l,nitrite_mg_l,alkalinity_mg_l,secchi_cm,sample_count,sample_kg,abw_g,dead,note");
        foreach (var s in History(max: int.MaxValue).OrderBy(x => x.Time))
            w.WriteLine(string.Join(',', s.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", ci), Q(s.PondId), Q(s.Sampler), N(s.TemperatureC), N(s.DissolvedOxygen),
                N(s.Ph), N(s.SalinityPpt), N(s.Ammonia), N(s.Nitrite), N(s.Alkalinity), N(s.TransparencyCm), s.SampleCount?.ToString(ci) ?? "",
                N(s.SampleWeightKg), N(s.AbwG), s.Dead?.ToString(ci) ?? "", Q(s.Note)));
    }

    private static string ToJson(FieldSample s)
    {
        var o = new JsonObject { ["pond"] = s.PondId, ["sampler"] = s.Sampler, ["time"] = s.Time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
        void Put(string k, double? v) { if (v is { } x) o[k] = x; }
        Put("temp", s.TemperatureC); Put("do", s.DissolvedOxygen); Put("ph", s.Ph); Put("sal", s.SalinityPpt); Put("tan", s.Ammonia);
        Put("no2", s.Nitrite); Put("alk", s.Alkalinity); Put("secchi", s.TransparencyCm); Put("count", s.SampleCount); Put("kg", s.SampleWeightKg); Put("dead", s.Dead);
        if (s.Note.Length > 0) o["note"] = s.Note;
        return o.ToJsonString();
    }

    private static FieldSample? FromJson(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            return new FieldSample
            {
                PondId = (string)o["pond"]!, Sampler = (string?)o["sampler"] ?? "",
                Time = DateTime.Parse((string)o["time"]!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                TemperatureC = (double?)o["temp"], DissolvedOxygen = (double?)o["do"], Ph = (double?)o["ph"], SalinityPpt = (double?)o["sal"], Ammonia = (double?)o["tan"],
                Nitrite = (double?)o["no2"], Alkalinity = (double?)o["alk"], TransparencyCm = (double?)o["secchi"], SampleCount = (int?)o["count"],
                SampleWeightKg = (double?)o["kg"], Dead = (int?)o["dead"], Note = (string?)o["note"] ?? "",
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return null; }   // a torn last line after a crash
    }

    public void Dispose() => Outbox.Dispose();
}
