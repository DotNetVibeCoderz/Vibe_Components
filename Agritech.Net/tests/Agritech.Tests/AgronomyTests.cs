using Agritech.AI;
using Agritech.Crops;
using Agritech.Native;
using Agritech.RemoteSensing;
using Agritech.Simulation;
using Agritech.Soil;
using Agritech.Weather;
using Agritech.Gallery.Shared;

namespace Agritech.Tests;

public class CropTests
{
    [Fact]
    public void Gdd_UsesBaseAndCutoff()
    {
        Assert.Equal(15, Phenology.GrowingDegreeDays(20, 30, 10, 30));
        Assert.Equal(15, Phenology.GrowingDegreeDays(20, 36, 10, 30)); // capped
        Assert.Equal(0, Phenology.GrowingDegreeDays(2, 8, 10, 30));
    }

    [Fact]
    public void Kc_FollowsFao56Curve()
    {
        var maize = CropCatalog.Default.Get("maize");
        Assert.Equal(maize.KcIni, maize.CropCoefficient(10));
        Assert.Equal(maize.KcMid, maize.CropCoefficient(70));
        Assert.InRange(maize.CropCoefficient(37), maize.KcIni, maize.KcMid);
    }

    [Fact]
    public void Catalog_FindsByIndonesianName()
    {
        Assert.True(CropCatalog.Default.TryGet("Padi", out var c));
        Assert.Equal("rice", c.Id);
    }

    [Fact]
    public void WaterLimitedYield_Fao33()
    {
        var maize = CropCatalog.Default.Get("maize");
        Assert.Equal(maize.PotentialYieldTonHa, YieldEstimator.WaterLimited(maize, 500, 500));
        // Ky 1.25, ETa/ETm 0.8 → Ya/Ym = 0.75
        Assert.Equal(7.5, YieldEstimator.WaterLimited(maize, 400, 500), 6);
    }

    [Fact]
    public void Recommender_PrefersRiceForWetLowlandAndPotatoForHighland()
    {
        var r = new CropRecommender();
        var lowland = r.Recommend(new SiteConditions(27, 2000, 5.5, 20), 3);
        Assert.Contains(lowland, s => s.Crop.Id == "rice");
        var highland = r.Recommend(new SiteConditions(17, 600, 5.5, 1600), 3);
        Assert.Contains(highland, s => s.Crop.Id == "potato");
    }

    [Fact]
    public void Lifecycle_ReachesMaturity()
    {
        var lc = new CropLifecycle(CropCatalog.Default.Get("mungbean"), new DateOnly(2026, 1, 1));
        while (!lc.IsMature) lc.Advance(23, 32);
        Assert.InRange(lc.DaysAfterPlanting, 55, 75);
    }
}

public class SoilTests
{
    [Theory]
    [InlineData(92, 4, 4, SoilTextureClass.Sand)]
    [InlineData(40, 40, 20, SoilTextureClass.Loam)]
    [InlineData(20, 60, 20, SoilTextureClass.SiltLoam)]
    [InlineData(20, 20, 60, SoilTextureClass.Clay)]
    [InlineData(30, 35, 35, SoilTextureClass.ClayLoam)]
    [InlineData(65, 20, 15, SoilTextureClass.SandyLoam)]
    public void UsdaTexture(double sand, double silt, double clay, SoilTextureClass expected) =>
        Assert.Equal(expected, SoilTexture.Classify(sand, silt, clay));

    [Fact]
    public void SaxtonRawls_LoamIsPlausible()
    {
        var h = SoilTexture.Hydraulics(40, 20, 2.5);
        Assert.InRange(h.FieldCapacity, 0.24, 0.32);
        Assert.InRange(h.WiltingPoint, 0.10, 0.16);
        Assert.InRange(h.AvailableWaterMmPerM, 100, 200);
    }

    [Fact]
    public void Health_AcidSoilGetsLimeRecommendation()
    {
        var r = SoilHealth.Assess(new SoilSample { Ph = 4.8 });
        Assert.Contains(r.Recommendations, x => x.Contains("lime", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(r.Score, 0, 100);
    }

    [Fact]
    public void FertilizerPlan_CoversRequirement()
    {
        var need = new NutrientNeed(120, 60, 60);
        var plan = FertilizerCalculator.Plan(need, 2);
        double n = plan.Lines.Sum(l => l.KgPerHa * l.Product.N / 100), p = plan.Lines.Sum(l => l.KgPerHa * l.Product.P2O5 / 100), k = plan.Lines.Sum(l => l.KgPerHa * l.Product.K2O / 100);
        Assert.InRange(n, 119, 121); Assert.InRange(p, 59, 61); Assert.InRange(k, 59, 61);
        Assert.All(plan.Lines, l => Assert.Equal(l.KgPerHa * 2, l.TotalKg, 1));
    }

    [Fact]
    public void Ledger_Balance()
    {
        var l = new NutrientLedger();
        l.ApplyFertilizer(new(2026, 1, 1), FertilizerProducts.Urea, 200);
        l.RecordHarvest(new(2026, 4, 1), 5, new NutrientNeed(16, 7, 20));
        Assert.Equal((12.0, -35.0, -100.0), l.Balance());
    }
}

public class WeatherTests
{
    [Fact]
    public void PenmanMonteith_Fao56Example18()
    {
        double es = (Evapotranspiration.SaturationVapourPressure(21.5) + Evapotranspiration.SaturationVapourPressure(12.3)) / 2;
        var w = new DailyWeather(new DateOnly(2026, 7, 6), 12.3, 21.5, 0, 1.409 / es * 100, 2.078, 22.07);
        double et0 = Evapotranspiration.PenmanMonteith(w, new WeatherStation(50.8, 100));
        Assert.InRange(et0, 3.75, 4.05);
    }

    [Fact]
    public void Ra_Fao56Example8()
    {
        // 3 Sep (DOY 246) at 20°S → Ra = 32.2 MJ/m²/day
        Assert.InRange(Evapotranspiration.ExtraterrestrialRadiation(-20, 246), 32.0, 32.4);
    }

    [Fact]
    public void Synthetic_IsDeterministicAndPlausible()
    {
        var gen = new SyntheticWeatherProvider(1, annualRainMm: 2000);
        var a = gen.Generate(new(-6.3, 107.3), new(2026, 1, 1), new(2026, 12, 31));
        var b = gen.Generate(new(-6.3, 107.3), new(2026, 1, 1), new(2026, 12, 31));
        Assert.Equal(a.Sum(d => d.RainMm), b.Sum(d => d.RainMm));
        Assert.InRange(a.Sum(d => d.RainMm), 1200, 3200);
        Assert.InRange(a.Average(d => d.TmeanC), 25, 29);
        // Pinned value: must be identical across processes/machines (regression for HashCode-based seeding).
        Assert.Equal(2538.6, Math.Round(a.Sum(d => d.RainMm), 1));
    }

    [Fact]
    public void Climate_DeltasIncreaseWithScenario()
    {
        Assert.True(ClimateProjection.Delta(ClimateScenario.Ssp585, 2080).TempChangeC > ClimateProjection.Delta(ClimateScenario.Ssp126, 2080).TempChangeC);
    }

    [Fact]
    public void OpenMeteoParser_ReadsDailyPayload()
    {
        var json = """{"daily":{"time":["2026-09-14","2026-09-15"],"temperature_2m_max":[32.1,31.0],"temperature_2m_min":[23.4,22.9],"precipitation_sum":[0.0,12.5],"relative_humidity_2m_mean":[74,88],"wind_speed_10m_max":[3.2,2.1],"shortwave_radiation_sum":[21.5,15.2],"et0_fao_evapotranspiration":[4.6,3.1]}}""";
        var d = OpenMeteoWeatherProvider.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement);
        Assert.Equal(2, d.Count);
        Assert.Equal(12.5, d[1].RainMm);
        Assert.Equal(4.6, d[0].Et0Mm);
    }
}

public class RemoteSensingTests
{
    [Fact]
    public void ManagedAndNativeKernelsAgree()
    {
        var rng = new Random(1);
        var a = Enumerable.Range(0, 100_003).Select(_ => (float)rng.NextDouble()).ToArray();
        var b = Enumerable.Range(0, 100_003).Select(_ => (float)rng.NextDouble()).ToArray();
        a[5] = 0; b[5] = 0;
        var native = new float[a.Length]; var managed = new float[a.Length];
        var prev = AgriKernels.PreferredBackend;
        try
        {
            AgriKernels.PreferredBackend = KernelBackend.Rust; AgriKernels.NormalizedDifference(a, b, native);
            AgriKernels.PreferredBackend = KernelBackend.Managed; AgriKernels.NormalizedDifference(a, b, managed);
        }
        finally { AgriKernels.PreferredBackend = prev; }
        Assert.True(float.IsNaN(native[5]) && float.IsNaN(managed[5]));
        for (int i = 0; i < a.Length; i += 97) if (i != 5) Assert.Equal(managed[i], native[i], 5);
        var s = AgriKernels.Statistics(native);
        Assert.Equal(a.Length - 1, s.Count);
    }

    [Fact]
    public void NativeLibrary_IsLoaded() => Assert.True(AgriKernels.IsNativeAvailable, "agritech_native should be copied to the output folder.");

    [Fact]
    public void Ndvi_OfSyntheticSceneIsVegetated()
    {
        var img = SyntheticScene.Create(DemoFarm.Field.Bounds, 10, 3, 0.7, 0);
        var ndvi = VegetationIndices.Ndvi(img);
        var s = ndvi.Statistics();
        Assert.InRange(s.Mean, 0.5, 0.9);
        var evi = VegetationIndices.Compute(img, VegetationIndex.Evi).Statistics();
        Assert.InRange(evi.Mean, 0.2, 1.0);
    }

    [Fact]
    public void Zonal_CountsPixelsInsidePolygon()
    {
        var z = RasterAnalysis.Zonal(VegetationIndices.Ndvi(SyntheticScene.Create(DemoFarm.Field.Bounds, 10)), DemoFarm.Field);
        double expected = DemoFarm.Field.AreaM2 / 100;
        Assert.InRange(z.PixelCount, expected * 0.85, expected * 1.15);
    }

    [Fact]
    public void Phenometrics_FindsSeason()
    {
        var start = new DateOnly(2026, 1, 1);
        var series = Enumerable.Range(0, 24).Select(i => (start.AddDays(i * 8), 0.2 + 0.6 * Math.Exp(-Math.Pow((i - 12) / 4.0, 2)))).ToList();
        var m = Phenometrics.Compute(series);
        Assert.Equal(start.AddDays(96), m.PeakDate);
        Assert.True(m.StartOfSeason < m.PeakDate && m.EndOfSeason > m.PeakDate);
    }

    [Fact]
    public void Sentinel2Scaling_AppliesBaselineOffset()
    {
        Assert.Equal(0.2f, SensorCatalog.ToReflectance(SatelliteSensor.Sentinel2, 3000, 5.0), 5);
        Assert.Equal(0.3f, SensorCatalog.ToReflectance(SatelliteSensor.Sentinel2, 3000, 3.0), 5);
    }
}

public class AiAndSimulationTests
{
    [Fact]
    public void RidgeRegression_RecoversCoefficients()
    {
        var rng = new Random(2);
        var x = Enumerable.Range(0, 200).Select(_ => new[] { rng.NextDouble() * 10, rng.NextDouble() * 5 }).ToArray();
        var y = x.Select(r => 3 + 2 * r[0] - 1.5 * r[1]).ToArray();
        var m = new RidgeRegression(1e-6).Fit(x, y);
        Assert.Equal(2, m.Coefficients[0], 3); Assert.Equal(-1.5, m.Coefficients[1], 3); Assert.Equal(3, m.Intercept, 3);
    }

    [Fact]
    public void YieldForecaster_CrossValidatesWell()
    {
        var eval = YieldForecaster.CrossValidate(YieldForecaster.SyntheticDataset(200));
        Assert.True(eval.R2 > 0.6, $"R2 = {eval.R2}");
    }

    [Fact]
    public void DiseaseRisk_RiceBlastHighWhenWarmAndWet()
    {
        var wet = Enumerable.Range(0, 7).Select(i => new InfectionConditions(new DateOnly(2026, 1, 1).AddDays(i), 14, 25, 95)).ToList();
        var dry = Enumerable.Range(0, 7).Select(i => new InfectionConditions(new DateOnly(2026, 1, 1).AddDays(i), 1, 33, 60)).ToList();
        Assert.True(DiseaseRiskModel.Assess(PathogenModel.RiceBlast, wet).Level >= RiskLevel.High);
        Assert.Equal(RiskLevel.Low, DiseaseRiskModel.Assess(PathogenModel.RiceBlast, dry).Level);
    }

    [Fact]
    public void Irrigation_RainfedDrySpellTriggersIrrigation()
    {
        var crop = CropCatalog.Default.Get("maize");
        var planting = new DateOnly(2026, 6, 1);
        var hist = Enumerable.Range(0, 50).Select(i => new DailyWeather(planting.AddDays(i), 22, 33, 0, 60, 2, 22)).ToList();
        var fc = Enumerable.Range(50, 7).Select(i => new DailyWeather(planting.AddDays(i), 22, 33, 0, 60, 2, 22)).ToList();
        var rec = new IrrigationAdvisor(crop, SoilTexture.Hydraulics(40, 20, 2), new WeatherStation(-6.3, 20)).Recommend(planting, hist, fc, 1);
        Assert.True(rec.IrrigateNow);
        Assert.True(rec.VolumeM3 > 100);
    }

    [Fact]
    public void FertilizerAdvisor_SchedulesSplitUrea()
    {
        var adv = FertilizerAdvisor.Advise(CropCatalog.Default.Get("rice"), DemoFarm.Soil, 2, 6);
        Assert.Equal(3, adv.Schedule.Count);
        Assert.NotEmpty(adv.DueWithinWeek(0));
    }

    [Fact]
    public void Simulation_IrrigationImprovesYieldInDrySeason()
    {
        var r = SimulationDemo.Run("maize", 120, years: 4);
        Assert.NotNull(r.Rainfed.MaturityDate);
        Assert.True(r.Irrigated.YieldTonHa >= r.Rainfed.YieldTonHa);
        Assert.InRange(r.Irrigated.YieldTonHa, 3, 14);
        Assert.True(r.Irrigated.TotalIrrigationMm > 0);
        Assert.Equal(5, r.Scenarios.Count);
    }

    [Fact]
    public void Simulation_NitrogenResponseIsPositive()
    {
        var crop = CropCatalog.Default.Get("rice");
        var wx = new SyntheticWeatherProvider(2, annualRainMm: 2600).Generate(DemoFarm.Center, new(2026, 11, 1), new(2027, 6, 1));
        var sim = new CropSimulator(crop, DemoFarm.Soil, DemoFarm.Station);
        var lowN = sim.Run(new Management { PlantingDate = new(2026, 11, 1), InitialMineralNKgHa = 10 }, wx);
        var highN = sim.Run(new Management { PlantingDate = new(2026, 11, 1), InitialMineralNKgHa = 10, NitrogenApplications = [(0, 40), (21, 50), (42, 40)] }, wx);
        Assert.True(highN.YieldTonHa > lowN.YieldTonHa, $"{highN.YieldTonHa} vs {lowN.YieldTonHa}");
    }
}
