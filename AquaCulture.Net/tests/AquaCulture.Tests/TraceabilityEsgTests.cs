using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Esg;
using AquaCulture.Gallery.Shared;
using AquaCulture.Traceability;

namespace AquaCulture.Tests;

public class TraceabilityTests
{
    [Fact]
    public void Gs1_check_digits_and_digital_link_uris()
    {
        Assert.Equal(1, Gs1.CheckDigit("400638133393"));             // GTIN-13 4006381333931
        Assert.True(Gs1.IsValid("4006381333931"));
        Assert.False(Gs1.IsValid("4006381333932"));
        Assert.True(Gs1.IsValid("106141411234567897"));              // GS1 example SSCC
        Assert.Equal("106141411234567897", Gs1.MakeSscc(1, "0614141", 123456789));
        Assert.Equal("https://id.gs1.org/01/04006381333931/10/LOT%2001", Gs1.Lot("4006381333931", "LOT 01"));
        string gln = Gs1.WithCheckDigit("899999900001");
        Assert.Equal($"https://id.gs1.org/414/{gln}/254/P-03", Gs1.Location(gln, "P-03"));
        Assert.Throws<ArgumentException>(() => Gs1.Lot("4006381333932", "x"));
    }

    private static (Farm Farm, CultureCycle Cycle, TraceabilitySetup Setup) Sample()
    {
        var c = new CultureCycle { Id = "C1", PondId = "P-03", Species = SpeciesCatalog.Vaname, StockingDate = new DateOnly(2026, 6, 1), StockedCount = 150_000, StockingWeightG = 0.01 };
        c.LogFeed(new DateOnly(2026, 6, 10), 50, "STARTER-A").LogFeed(new DateOnly(2026, 7, 20), 900, "GROWER-B").LogFeed(new DateOnly(2026, 8, 20), 1200, "GROWER-B");
        c.LogHarvest(new DateOnly(2026, 7, 30), 600, 60_000, 60_000, partial: true).LogHarvest(new DateOnly(2026, 8, 30), 1500, 70_000, 75_000);
        var farm = new Farm { Id = "TMS", Name = "Tambak Mina Sejahtera" };
        farm.Cycles.Add(c);
        var setup = new TraceabilitySetup(Gs1.WithCheckDigit("899999900001"), Gs1.WithCheckDigit("899999900002"), Gs1.WithCheckDigit("899999910001"),
            Gs1.WithCheckDigit("899999910002"), "8999999")
        {
            FeedLots = new Dictionary<string, (string, string)> { ["GROWER-B"] = (Gs1.WithCheckDigit("899999920001"), "GB-2607") },
            DefaultFeedGtin = Gs1.WithCheckDigit("899999920002"),
        };
        return (farm, c, setup);
    }

    [Fact]
    public void Cycle_events_round_trip_through_epcis_json_ld()
    {
        var (farm, c, s) = Sample();
        var events = FarmEpcis.CycleEvents(farm, c, s, "PL10-2605-A");
        Assert.Equal(3 + 2, events.Count);                          // commissioning, shipping, receiving + 2 harvests
        var json = EpcisJson.ToDocument(events, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(7)));
        Assert.Contains("\"type\": \"EPCISDocument\"", json);
        Assert.Contains(EpcisJson.Context, json);
        Assert.Contains("https://ref.gs1.org/cbv/BizStep-receiving", json);
        var back = EpcisJson.ParseDocument(json);
        Assert.Equal(events.Count, back.Count);
        var harvest = back.Last();
        Assert.Equal(EpcisEventType.TransformationEvent, harvest.Type);
        Assert.Equal(1500, harvest.Outputs.Single().Quantity);
        Assert.Equal("AQUACULTURE", harvest.Ilmd["cbvmda:productionMethodForFishAndSeafoodCode"]);
        Assert.Equal("Litopenaeus vannamei", harvest.Ilmd["cbvmda:speciesForFisheryStatisticsPurposesName"]);
        Assert.Contains(harvest.Inputs, i => i.EpcClass.EndsWith("/10/GB-2607", StringComparison.Ordinal) && i.Quantity == 1200);
        Assert.Equal(events[0].EventTime, back[0].EventTime);
    }

    [Fact]
    public void Trace_back_and_recall_follow_feed_lots_to_pallets()
    {
        var (farm, c, s) = Sample();
        var events = FarmEpcis.CycleEvents(farm, c, s, "PL10-2605-A").ToList();
        string lot1 = Gs1.Lot(s.HarvestGtin, FarmEpcis.HarvestLot(farm.Id, c, new DateOnly(2026, 7, 30)));
        string lot2 = Gs1.Lot(s.HarvestGtin, FarmEpcis.HarvestLot(farm.Id, c, new DateOnly(2026, 8, 30)));
        string sscc = Gs1.MakeSscc(3, "8999999", 42);
        var plant = Gs1.Location(Gs1.WithCheckDigit("899999900003"));
        events.AddRange(FarmEpcis.PackAndShip(sscc, [new(lot2, 1000)], Gs1.Location(s.FarmGln), plant, new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.FromHours(7))));
        var g = new TraceGraph(events);

        var back = g.TraceBack(Gs1.Sscc(sscc));
        Assert.Contains(lot2, back);
        Assert.Contains(Gs1.Lot(s.SeedGtin, "PL10-2605-A"), back);
        Assert.Contains(Gs1.Lot(s.FeedLots["GROWER-B"].Gtin, "GB-2607"), back);
        Assert.DoesNotContain(lot1, back);

        // a problem with the GROWER-B lot reaches both harvests' feed? no: lot 1 used GROWER-B from 20 Jul, lot 2 from 20 Aug — both
        var recall = g.TraceForward(Gs1.Lot(s.FeedLots["GROWER-B"].Gtin, "GB-2607"));
        Assert.Contains(lot1, recall);
        Assert.Contains(lot2, recall);
        Assert.Contains(Gs1.Sscc(sscc), recall);
        // the starter feed only reached the first (partial) harvest
        var starter = g.TraceForward(Gs1.Lot(s.DefaultFeedGtin!, "STARTER-A"));
        Assert.Contains(lot1, starter);
        Assert.DoesNotContain(lot2, starter);

        Assert.Equal(3, g.History(Gs1.Sscc(sscc)).Count);            // packing, shipping, receiving
        Assert.Equal("P-03", g.MasterData(lot2)["aqua:pond"]);
    }
}

public class EsgTests
{
    [Fact]
    public void Cycle_footprint_adds_up_and_mangrove_conversion_is_amortised()
    {
        var c = new CultureCycle { Id = "C1", PondId = "P-01", Species = SpeciesCatalog.Vaname, StockingDate = new DateOnly(2026, 1, 1), StockedCount = 200_000, StockingWeightG = 0.01 };
        c.LogFeed(new DateOnly(2026, 2, 1), 4500).LogHarvest(new DateOnly(2026, 4, 1), 3000, 150_000, 70_000);
        var r = new CycleResources { ElectricityKwh = 9000, DieselLitres = 200, WaterIntakeM3 = 30_000 };
        var e = EsgAccounting.Cycle(c, r);
        Assert.Equal(90, e.Days);
        Assert.Equal(4500 * 1.8, e.FeedCo2Kg, 6);
        Assert.Equal(9000 * 0.85, e.ElectricityCo2Kg, 6);
        Assert.Equal(200 * 2.68, e.DieselCo2Kg, 6);
        Assert.Equal(e.FeedCo2Kg + e.ElectricityCo2Kg + e.DieselCo2Kg, e.TotalCo2eKg, 1);
        Assert.Equal(e.TotalCo2eKg / 3000, e.Co2ePerKg, 3);
        Assert.Equal(1.5, e.Fcr, 6);
        Assert.Equal(3.0, e.EnergyKwhPerKg, 6);
        Assert.Equal(10, e.WaterM3PerKg, 6);
        // N: feed 4500 × 0.35 / 6.25 = 252 kg; harvest 3000 × 0.029 = 87 kg → ≈ 165 kg discharged
        Assert.InRange(e.NitrogenDischargeKg, 164, 166);

        var cleared = EsgAccounting.Cycle(c, r with { ConvertedMangroveHa = 1, FarmAgeYears = 5 });
        double expected = 1023 * 44.0 / 12 * 1000 / 20 * 90 / 365;      // ≈ 46 t CO₂ for one hectare over 90 days
        Assert.Equal(expected, cleared.LandUseCo2Kg, 0);
        Assert.True(cleared.LandUseCo2Kg > cleared.FeedCo2Kg, "mangrove conversion becomes the largest source");
        Assert.Equal(0, EsgAccounting.Cycle(c, r with { ConvertedMangroveHa = 1, FarmAgeYears = 25 }).LandUseCo2Kg);
        Assert.True(EsgAccounting.Cycle(c, r with { RestoredMangroveHa = 2 }).TotalCo2eKg < e.TotalCo2eKg);
    }

    [Fact]
    public void Farm_report_aggregates_cycles_and_names_the_main_lever()
    {
        var farm = new Farm { Id = "F" };
        farm.Ponds.Add(new Pond { Id = "P-01", AreaM2 = 2000, DepthM = 1.3, AerationKw = 4.4 });
        farm.Ponds.Add(new Pond { Id = "P-02", AreaM2 = 2500, DepthM = 1.4, AerationKw = 5.5 });
        foreach (var (id, pond, fcr) in new[] { ("C1", "P-01", 1.3), ("C2", "P-02", 1.8) })
        {
            var c = new CultureCycle { Id = id, PondId = pond, Species = SpeciesCatalog.Vaname, StockingDate = new DateOnly(2026, 1, 1), StockedCount = 200_000, StockingWeightG = 0.01 };
            c.LogFeed(new DateOnly(2026, 2, 1), 3000 * fcr).LogHarvest(new DateOnly(2026, 4, 10), 3000, 150_000, 70_000);
            farm.Cycles.Add(c);
        }
        var report = EsgAccounting.Farm(farm, c => new CycleResources
        {
            ElectricityKwh = farm.Pond(c.PondId).AerationKw * 18 * 100,
            WaterIntakeM3 = EsgAccounting.PondWaterUse(farm.Pond(c.PondId).AreaM2, farm.Pond(c.PondId).DepthM, 100, 5),
        });
        Assert.NotEmpty(report.Cycles);
        Assert.Equal(report.Cycles.Sum(c => c.HarvestKg), report.HarvestKg, 1);
        Assert.InRange(report.Co2ePerKg, 1, 10);
        Assert.Contains(report.Findings, f => f.En.StartsWith("Largest emission source: Feed", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.En.StartsWith("FCR", StringComparison.Ordinal));          // harvest-weighted FCR 1.55 > 1.5
        Assert.Throws<InvalidOperationException>(() => EsgAccounting.Farm(new Farm { Id = "empty" }, _ => new CycleResources()));
    }
}
