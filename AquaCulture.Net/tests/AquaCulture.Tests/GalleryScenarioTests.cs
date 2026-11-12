using AquaCulture.Gallery.Shared;
using AquaCulture.Reporting;

namespace AquaCulture.Tests;

/// <summary>Every gallery use case must run and produce well-formed output (the Blazor and Avalonia apps render these results).</summary>
public class GalleryScenarioTests
{
    private static void Svg(string s) => System.Xml.Linq.XDocument.Parse(s);

    [Fact]
    public void Catalog_is_complete_and_bilingual()
    {
        Assert.Equal(20, GalleryCatalog.Demos.Length);
        Assert.All(GalleryCatalog.Demos, d => { Assert.NotEqual(d.Title.En, d.Title.Id); Assert.Contains(GalleryCatalog.Groups, g => g.Key == d.Group); });
        Assert.Equal(6, DemoFarm.Farm.Ponds.Count);
    }

    [Fact] public void Water() { var r = WaterDemo.Run(); Svg(r.GaugeSvg); Svg(r.DaySvg); Assert.NotEmpty(r.Actions); }
    [Fact] public void Oxygen() { var r = OxygenDemo.Run(); Svg(r.ChartSvg); Svg(r.BudgetSvg); Assert.True(r.On.MinDo > r.Off.MinDo); }
    [Fact] public void Hydro() { var r = HydroDemo.Run(hours: 1); Svg(r.FlowSvg); Assert.True(r.Max > r.Min); Assert.True(r.Mean > 0); }
    [Fact] public void Growth() { var r = GrowthDemo.Run(); Svg(r.WeightSvg); Svg(r.HistogramSvg); Assert.True(r.Optimal.Margin > 0); Assert.True(r.MonteCarlo.DayP50 > 0); }
    [Fact] public void Feeding() { var r = FeedingDemo.Run(); Svg(r.FcrSvg); Assert.True(r.Plan.DailyKg > 0); Assert.NotEmpty(r.Ranking); }
    [Fact] public void Ai() { var r = AiDemo.Run(); Svg(r.ImportanceSvg); Svg(r.PolicySvg); Assert.True(r.GrowthR2 > 0.7); Assert.True(r.MortalityAuc > 0.8); }
    [Fact] public void Disease() { var r = DiseaseDemo.Run("vaname", DiseaseDemo.DefaultShrimpSigns); Svg(r.ProbabilitySvg); Assert.Equal("wssv", r.Diagnosis.Top!.Disease.Id); }
    [Fact] public void Vision() { var r = VisionDemo.Run(); Assert.InRange(r.Result.Count, r.TruthCount - 2, r.TruthCount + 1); }
    [Fact] public void Twin() { var r = TwinDemo.Run(12); Svg(r.MapSvg); Svg(r.HeatSvg); Assert.Equal(6, r.States.Count); Assert.NotEmpty(r.Decisions); }
    [Fact] public void Iot() { var r = IoTDemo.Run(); Svg(r.EdgeSvg); Assert.InRange(r.Outliers, 2, 3); Assert.Equal(5.12, r.ModbusValues[0].Value, 6); Assert.Equal(5, r.MqttReadings.Count); /* temp, do, ph, sal, rssi */ }
    [Fact] public void Gis() { var r = GisDemo.Run(); Svg(r.MapSvg); Assert.True(r.BufferAreaM2 > r.FarmAreaM2); Assert.Equal("sea-intake", r.Sources[0].Source.Id); /* brackish intake suits whiteleg shrimp */ }
    [Fact] public void Weather() { var r = WeatherDemo.Run(); Svg(r.ForecastSvg); Svg(r.MatrixSvg); Assert.Equal(6, r.Climate.Hazards.Count); }
    [Fact] public void Connectors() { var r = ConnectorsDemo.Run(); Svg(r.BmkgSvg); Svg(r.NasaSvg); Svg(r.MarineSvg); Svg(r.StoreSvg); Assert.Equal("Kemayoran", r.Bmkg.Location.Village); Assert.Equal(31, r.NasaDays.Count); Assert.Equal(r.StoredReadings / 2, r.QueriedReadings); Assert.True(r.ParquetBytes < r.CsvBytes); Assert.Equal("P-03", r.ChirpStack[0].PondId); }
    [Fact] public void Chlorophyll() { var r = ChlorophyllDemo.Run(); Svg(r.ZonesSvg); Svg(r.LegendSvg); Assert.True(r.WaterAreaHa > 100); Assert.All(r.Zones, z => Assert.True(z.MeanChl > r.Stats.Median, z.Zone)); Assert.True(ChlorophyllDemo.Run(cageLoad: 0).Zones.Max(z => z.MeanChl) < r.Zones.Min(z => z.MeanChl)); }
    [Fact] public void Learning() { var r = LearningDemo.Run(); Svg(r.ForecastSvg); Svg(r.SkillSvg); Assert.Equal(12, r.Curve.Point.Length); Assert.True(r.Skill[5].SkillScore > 0); Assert.Equal(0, r.Aeration[0].HoursBelowCritical); }
    [Fact] public void Stock() { var r = StockDemo.Run(); Svg(r.KobeSvg); Svg(r.YprSvg); Assert.True(r.Fit.Msy > 0); Assert.Equal(2.4, r.Z, 1); }
    [Fact] public void Population() { var r = PopulationDemo.Run(); Svg(r.BiomassSvg); Svg(r.PredatorSvg); Assert.True(r.ConstantF[^1].SpawningBiomass < r.NoFishing[^1].SpawningBiomass); }
    [Fact] public void Analytics() { var r = AnalyticsDemo.Run(); Svg(r.CostSvg); Assert.Equal(6, r.Summary.Cycles.Count); Assert.True(r.Selected.Revenue > 0); }

    [Fact]
    public void Reporting()
    {
        foreach (var s in Enum.GetValues<ComplianceStandard>())
        {
            var r = ReportingDemo.Run(s, lang: "id");
            Assert.Equal(7, r.Files.Count);
            Assert.All(r.Files, f => Assert.True(f.Bytes.Length > 50, f.Name));
            Assert.NotEqual(ReadinessLevel.Ready, r.Compliance.Readiness);
        }
    }

    [Fact]
    public async Task Agent_offline()
    {
        var agent = AgentDemo.Create(new AquaCulture.AI.Agent.RuleBasedChatModel());
        foreach (var (en, id) in AgentDemo.Suggestions)
        {
            Assert.False(string.IsNullOrWhiteSpace((await agent.AskAsync(en)).Text), en);
            Assert.False(string.IsNullOrWhiteSpace((await agent.AskAsync(id)).Text), id);
        }
    }
}
