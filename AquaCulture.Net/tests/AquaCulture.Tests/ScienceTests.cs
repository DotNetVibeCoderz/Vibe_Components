using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.Forecasting;
using AquaCulture.Hydrodynamic;
using AquaCulture.Population;
using AquaCulture.StockAssessment;

namespace AquaCulture.Tests;

public class StockAssessmentTests
{
    private static List<CatchEffort> SchaeferData(double r = 0.6, double k = 10_000, double q = 0.0005)
    {
        var data = new List<CatchEffort>();
        double b = k;
        for (int y = 0; y < 25; y++)
        {
            double effort = 100 + 40 * y;
            double c = q * effort * b;
            data.Add(new CatchEffort(2000 + y, c, effort));
            b = Math.Max(1, b + r * b * (1 - b / k) - c);
        }
        return data;
    }

    [Fact]
    public void Dynamic_schaefer_recovers_true_parameters()
    {
        var fit = SurplusProduction.FitDynamic(SchaeferData());
        Assert.Equal(0.6, fit.R, 1);
        Assert.InRange(fit.K, 9_000, 11_000);
        Assert.Equal(fit.R * fit.K / 4, fit.Msy, 6);
        Assert.True(fit.Rmse < 0.01);
    }

    [Fact]
    public void Equilibrium_models_give_positive_msy()
    {
        var data = SchaeferData();
        var s = SurplusProduction.Schaefer(data);
        var f = SurplusProduction.Fox(data);
        Assert.True(s.Msy > 0 && f.Msy > 0);
        Assert.True(s.EffortAtMsy > 0);
        Assert.Equal(s.Msy, s.YieldAt(s.EffortAtMsy), 6);
    }

    [Fact]
    public void Yield_per_recruit_reference_points()
    {
        var p = new YprParameters(WInfinityG: 2000, K: 0.3, T0: -0.2, M: 0.4, Tr: 0.5, Tc: 1.5);
        var r = YieldPerRecruit.Analyze(p);
        Assert.True(r.F01 < r.Fmax);
        Assert.True(r.YprAtF01 < r.YprAtFmax && r.YprAtF01 > 0.8 * r.YprAtFmax);
        Assert.Equal(0.4, r.SprAtF40, 2);
    }

    [Fact]
    public void Pauly_m_and_catch_curve()
    {
        Assert.InRange(LengthBased.PaulyM(45, 0.6, 28), 0.9, 1.6);
        // synthetic LFQ from an exponential decline with Z = 1.5
        double linf = 45, k = 0.6, z = 1.5;
        var lfq = new List<(double, double)>();
        for (double l = 6; l <= 42; l += 2)
        {
            double age(double x) => -Math.Log(1 - x / linf) / k;
            double n = 1e5 * (Math.Exp(-z * age(l - 1)) - Math.Exp(-z * age(l + 1)));
            lfq.Add((l, l < 12 ? n * (l - 4) / 8 : n)); // ascending selection limb
        }
        var cc = LengthBased.CatchCurve(lfq, linf, k);
        Assert.Equal(z, cc.Z, 1);
        var (f, e) = LengthBased.Exploitation(cc.Z, 0.9);
        Assert.Equal(cc.Z - 0.9, f, 9);
        Assert.InRange(e, 0, 1);
    }

    [Fact]
    public void Stock_recruitment_fits()
    {
        var data = Enumerable.Range(1, 20).Select(i => (S: i * 50.0, R: StockRecruitment.BevertonHolt(5, 0.002, i * 50.0))).ToList();
        var (a, b, r2) = StockRecruitment.FitBevertonHolt(data);
        Assert.Equal(5, a, 6); Assert.Equal(0.002, b, 8); Assert.Equal(1, r2, 6);
    }
}

public class PopulationTests
{
    [Fact]
    public void Leslie_lambda_and_stable_distribution()
    {
        var m = new LeslieMatrix([0, 1.5, 1.0], [0.5, 0.4]);
        var (lambda, sad) = m.Dominant();
        // characteristic equation λ³ = 0.75 λ + 0.2
        Assert.Equal(0, Math.Pow(lambda, 3) - 0.75 * lambda - 0.2, 6);
        Assert.Equal(1, sad.Sum(), 9);
    }

    [Fact]
    public void Fishing_reduces_biomass_and_hockey_stick_stops_at_blim()
    {
        var p = new AgeStructuredParameters
        {
            WeightAtAge = [0.05, 0.3, 0.8, 1.4, 2.0], MaturityAtAge = [0, 0.2, 0.8, 1, 1], M = 0.3,
            Recruitment = new RecruitmentModel.BevertonHolt(800, 0.0008),
        };
        var model = new AgeStructuredModel(p);
        var init = model.Equilibrium(500);
        var none = model.Run(init, 40, new HarvestPolicy.None());
        var fished = model.Run(init, 40, new HarvestPolicy.ConstantF(0.6));
        Assert.True(fished[^1].Biomass < none[^1].Biomass);
        Assert.True(fished.Sum(x => x.Catch) > 0);
        Assert.Equal(0, new HarvestPolicy.HockeyStick(0.5, 1000, 300).FishingMortality(250, 0));
    }

    [Fact]
    public void Predator_prey_oscillates_and_stays_positive()
    {
        var sim = PredatorPrey.Simulate(40, 9, 1.0, 100, 0.1, 0.1, 0.5, 0.3, 100);
        Assert.All(sim, x => Assert.True(x.Prey >= 0 && x.Predator >= 0));
        Assert.True(sim.Max(x => x.Prey) - sim.Min(x => x.Prey) > 10);
    }
}

public class EnvironmentAndHydroTests
{
    [Fact]
    public void Synthetic_weather_is_deterministic_and_tropical()
    {
        var a = SyntheticWeather.Generate(-6.3, new DateTime(2026, 1, 1), 30, 5);
        var b = SyntheticWeather.Generate(-6.3, new DateTime(2026, 1, 1), 30, 5);
        Assert.Equal(a.Select(x => x.AirTempC), b.Select(x => x.AirTempC));
        Assert.InRange(a.Average(x => x.AirTempC), 24, 30);
        Assert.True(a.Sum(x => x.RainfallMm) > 50); // January is wet
        Assert.Equal(0, a.Where(x => x.Time.Hour is 0 or 23).Max(x => x.SolarWm2));
    }

    [Fact]
    public void Heavy_rain_dilutes_salinity()
    {
        var w = Enumerable.Range(0, 24).Select(h => new WeatherObservation(new DateTime(2026, 1, 1).AddHours(h), 27, h == 15 ? 120 : 0, 85, 2, 100)).ToList();
        var r = WeatherImpactPredictor.Predict(w, new PondExposure(1.2, 25, Core.Farming.CultureSystem.EarthenPond, true));
        Assert.True(r.SalinityDropPpt > 2);
        Assert.Contains(r.Impacts, i => i.Code == "dilution");
        Assert.Contains(r.Impacts, i => i.Code == "acid-runoff");
    }

    [Fact]
    public void Climate_risk_ranks_storms_for_cages()
    {
        var daily = DailyWeather.FromHourly(SyntheticWeather.Generate(-6.3, new DateTime(2025, 1, 1), 365, 2));
        var stormy = daily.Select((d, i) => i % 20 == 0 ? d with { MaxWindMs = 15 } : d).ToList();
        var r = ClimateRiskAnalysis.Analyze(stormy, Core.Farming.CultureSystem.FloatingNetCage);
        Assert.Equal("storm", r.Top!.Code);
    }

    [Fact]
    public void Oxygen_budget_shows_diurnal_cycle_and_aeration_helps()
    {
        var w = SyntheticWeather.Generate(-6.3, new DateTime(2026, 7, 1), 2, 3);
        var p = new OxygenBudgetParameters { VolumeM3 = 2600, DepthM = 1.3, BiomassKg = 3000, MeanWeightG = 12, Group = SpeciesGroup.Shrimp, ChlorophyllUgL = 120, SalinityPpt = 20, AeratorKw = 6 };
        var off = PondOxygenBudget.Simulate(p, w, 6);
        var on = PondOxygenBudget.Simulate(p, w, 6, PondOxygenBudget.SetPoint(4.5));
        Assert.True(off.MaxDo - off.MinDo > 2);
        Assert.InRange(off.MinTime.Hour, 0, 8);
        Assert.True(on.MinDo > off.MinDo);
        Assert.True(on.AeratorKwh > 0);
    }

    [Fact]
    public void Paddle_wheel_transport_spreads_oxygen()
    {
        var g = new Grid2D(40, 30, 1).WithDikes();
        FlowFields.PaddleWheelCirculation(g, 0.15, [(8, 15, 0.2), (32, 15, 0.2)]);
        var t = new ScalarTransport(g) { Diffusivity = 0.02, Decay = 1e-4, Equilibrium = 7.5 };
        t.Sources.Add(new ScalarSource(8, 15, 0.02, 2));
        var init = Enumerable.Repeat(3f, g.Count).ToArray();
        var r = t.Run(init, 3600);
        Assert.True(r.Mean > 3);
        Assert.True(r.Max > r.Min);
        Assert.Equal(KernelBackendName(), r.Backend.ToString());
        static string KernelBackendName() => Native.AquaKernels.Backend.ToString();
    }

    [Fact]
    public void Ras_mass_balance_matches_timmons()
    {
        var d = RasMassBalance.Design(feedKgPerDay: 10, proteinPct: 40, tankVolumeM3: 20, targetTanMgL: 1, biofilterEfficiency: 0.5);
        Assert.Equal(368, d.TanProductionGPerDay, 6); // 10 kg × 0.40 × 0.092
        Assert.Equal(736, d.FlowM3PerHour * 24, 6);
        Assert.Equal(5, d.OxygenDemandKgPerDay, 6);
    }

    [Fact]
    public void Plume_and_settling()
    {
        Assert.True(Sediment.StokesVelocity(100e-6) > Sediment.StokesVelocity(50e-6));
        Assert.True(Sediment.PlumeConcentration(1, 5, 0.1, 0.5, 50, 0) > Sediment.PlumeConcentration(1, 5, 0.1, 0.5, 50, 20));
    }
}

public class ForecastingTests
{
    [Fact]
    public void Holt_extrapolates_a_trend()
    {
        var y = Enumerable.Range(0, 30).Select(i => 10 + 0.5 * i).ToArray();
        var f = new HoltLinear().Forecast(y, 5);
        Assert.Equal(10 + 0.5 * 34, f.Point[^1], 1);
        Assert.True(f.Upper[^1] >= f.Point[^1] && f.Lower[^1] <= f.Point[^1]);
    }

    [Fact]
    public void Holt_winters_captures_diurnal_do()
    {
        var y = Enumerable.Range(0, 24 * 6).Select(h => 5 + 2 * Math.Sin(2 * Math.PI * h / 24)).ToArray();
        var f = new HoltWinters(24).Forecast(y, 24);
        Assert.Equal(5 + 2 * Math.Sin(2 * Math.PI * (24 * 6 + 6) / 24), f.Point[6], 1);
    }

    [Fact]
    public void Ar_model_and_auto_selection()
    {
        var rng = new DeterministicRandom(4);
        var y = new double[3000]; for (int i = 2; i < y.Length; i++) y[i] = 0.6 * y[i - 1] - 0.2 * y[i - 2] + rng.NextGaussian() * 0.1;
        var ar = new Autoregressive(2); ar.Forecast(y, 3);
        Assert.Equal(0.6, ar.Coefficients[0], 1);
        Assert.Equal(-0.2, ar.Coefficients[1], 1);
        Assert.Equal(5, AutoForecaster.Forecast(y, 5).Point.Length);
    }

    [Fact]
    public void Do_crash_forecast_from_evening_decline()
    {
        var ts = new TimeSeries("DO", "mg/L");
        var t0 = new DateTime(2026, 3, 1, 18, 0, 0);
        for (int i = 0; i <= 8; i++) ts.Add(t0.AddMinutes(30 * i), 7.5 - 0.4 * i); // 0.8 mg/L per hour after dusk
        var f = DoCrashForecaster.Forecast(ts, SpeciesCatalog.Vaname, now: t0.AddHours(4));
        Assert.True(f.PredictedMin < 3);
        Assert.True(f.ProbabilityBelowCritical > 0.5);
        Assert.NotNull(f.AeratorStartBy);
        Assert.Equal(0.5, DoCrashForecaster.NormalCdf(0), 6);
    }

    [Fact]
    public void Harvest_forecast_percentiles_are_ordered_and_reproducible()
    {
        var temps = Enumerable.Repeat(29.0, 200).ToArray();
        var a = HarvestForecaster.Forecast(SpeciesCatalog.Nila, new DateOnly(2026, 1, 1), 20, 10_000, 400, temps, 200);
        var b = HarvestForecaster.Forecast(SpeciesCatalog.Nila, new DateOnly(2026, 1, 1), 20, 10_000, 400, temps, 200);
        Assert.True(a.DayP10 <= a.DayP50 && a.DayP50 <= a.DayP90);
        Assert.Equal(a.FinalWeights, b.FinalWeights);
        Assert.True(a.BiomassAtHorizonP10Kg <= a.BiomassAtHorizonP90Kg);
    }
}
