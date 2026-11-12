using AquaCulture.FieldApp;
using AquaCulture.IoT;
using AquaCulture.WaterQuality;

namespace AquaCulture.Tests;

public class FieldAppTests
{
    private static readonly FieldPond Pond = new("P-03", "Petak 3", "vaname", AreaM2: 2000, DepthM: 1.3, Population: 180_000, AerationKw: 4);

    [Fact]
    public void Sampling_gives_abw_biomass_feed_and_dosed_actions()
    {
        var s = new FieldSample
        {
            PondId = "P-03", Time = new DateTime(2026, 8, 1, 5, 30, 0, DateTimeKind.Utc), TemperatureC = 29, DissolvedOxygen = 3.2, Ph = 8.6, SalinityPpt = 18,
            Ammonia = 1.8, Alkalinity = 90, TransparencyCm = 20, SampleCount = 120, SampleWeightKg = 1.5, Dead = 35,
        };
        var r = FieldAnalysis.Evaluate(s, Pond);
        Assert.Equal(12.5, r.AbwG!.Value, 6);                                   // 1.5 kg / 120 animals
        Assert.Equal(2250, r.BiomassKg!.Value, 6);                              // 12.5 g × 180 000
        Assert.False(r.Water.IsHealthy);
        Assert.NotEmpty(r.Actions);
        Assert.NotNull(r.Feed);
        var unadjusted = AquaCulture.Feeding.FeedingCalculator.Plan(Pond.Species, 12.5, Pond.Population, 29);
        Assert.True(r.Feed!.DailyKg < unadjusted.DailyKg);                      // low DO and NH₃ cut the ration
        Assert.Contains(r.Notes, n => n.En.Contains("Secchi < 25"));
        Assert.Empty(r.EntryWarnings);
    }

    [Fact]
    public void Entry_checks_catch_typos_and_half_filled_samplings()
    {
        var s = new FieldSample { PondId = "P-03", Ph = 78, DissolvedOxygen = 5, SampleCount = 50 };   // "7.8" typed without the dot
        var w = s.Check();
        Assert.Contains(w, x => x.En.StartsWith("pH 78"));
        Assert.Contains(w, x => x.Id.Contains("jumlah ekor"));
        Assert.Null(s.AbwG);
    }

    [Fact]
    public void Journal_queues_readings_keeps_history_and_exports_csv()
    {
        string dir = Directory.CreateTempSubdirectory("aqua-field-").FullName;
        var t0 = new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc);
        using (var j = new FieldJournal(dir, "field-ani"))
        {
            j.SavePonds([Pond, Pond with { Id = "P-04", Name = "Petak 4", Population = 150_000 }]);
            Assert.Equal(5, j.Save(new FieldSample { PondId = "P-03", Sampler = "Ani", Time = t0, TemperatureC = 29, DissolvedOxygen = 4.1, Ph = 7.9, SampleCount = 100, SampleWeightKg = 1.0, Dead = 3 }));
            j.Save(new FieldSample { PondId = "P-03", Sampler = "Ani", Time = t0.AddDays(7), TemperatureC = 30, SampleCount = 100, SampleWeightKg = 1.35, Note = "air hijau, \"bloom\"" });
            j.Save(new FieldSample { PondId = "P-04", Sampler = "Ani", Time = t0.AddDays(7), DissolvedOxygen = 5.2 });
        }
        using var again = new FieldJournal(dir, "field-ani");                  // app restarted
        Assert.Equal(2, again.LoadPonds().Count);
        Assert.Equal(150_000, again.LoadPonds()[1].Population);
        var hist = again.History("P-03");
        Assert.Equal(2, hist.Count);
        Assert.Equal(13.5, hist[0].AbwG!.Value, 6);                            // newest first
        Assert.Equal(5 + 2 + 1, again.Outbox.Pending);              // temp, DO, pH, ABW, dead · temp, ABW · DO
        var queued = again.Outbox.Peek();
        Assert.Contains(queued, r => r.Reading.Kind == SensorKind.AverageWeight && r.Reading.Value == 10 && r.Reading.DeviceId == "field-ani");
        Assert.Contains(queued, r => r.Reading.Kind == SensorKind.Mortality && r.Reading.Value == 3);

        var report = FieldAnalysis.Evaluate(hist[0], again.LoadPonds()[0], previous: hist[1]);
        Assert.Contains(report.Notes, n => n.En.Contains("0.50 g/day"));        // (13.5 − 10) / 7

        var csv = new StringWriter();
        again.ExportCsv(csv);
        var lines = csv.ToString().TrimEnd().Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("time_utc,pond", lines[0]);
        Assert.Contains("\"air hijau, \"\"bloom\"\"\"", lines[2]);
        Assert.Contains(",13.5,", lines[2]);
    }

    [Fact]
    public void Field_kinds_parse_from_telemetry_keys()
    {
        Assert.True(SensorReading.TryParseKind("secchi", out var k1)); Assert.Equal(SensorKind.Transparency, k1);
        Assert.True(SensorReading.TryParseKind("abw", out var k2)); Assert.Equal(SensorKind.AverageWeight, k2);
        Assert.True(SensorReading.TryParseKind("kematian", out var k3)); Assert.Equal(SensorKind.Mortality, k3);
        Assert.Equal("g", SensorReading.DefaultUnit(SensorKind.AverageWeight));
    }
}
