using AquaCulture.Environment;
using AquaCulture.Hydrodynamic;

namespace AquaCulture.Tests;

public class ReservoirTests
{
    internal static List<WeatherObservation> Calm(DateTime t0, int days) => Enumerable.Range(0, days * 24).Select(h =>
    {
        var t = t0.AddHours(h); double hr = t.Hour;
        double solar = hr is > 6 and < 18 ? 900 * Math.Sin(Math.PI * (hr - 6) / 12) : 0;
        return new WeatherObservation(t, 28 + 3.5 * Math.Sin(2 * Math.PI * (hr - 9) / 24), 0, 70, 1.2 + 0.8 * Math.Sin(2 * Math.PI * (hr - 14) / 24), solar, 1008, 30);
    }).ToList();

    internal static List<WeatherObservation> Storm(DateTime t0, int days) => Enumerable.Range(0, days * 24).Select(h =>
    {
        var t = t0.AddHours(h); double hr = t.Hour;
        double solar = hr is > 6 and < 18 ? 200 * Math.Sin(Math.PI * (hr - 6) / 12) : 0;
        return new WeatherObservation(t, 22.5, hr % 6 < 2 ? 6 : 0.5, 95, 7.5, solar, 1004, 100);
    }).ToList();

    [Fact]
    public void Density_peaks_near_4_degrees_and_falls_in_warm_water()
    {
        Assert.True(ReservoirStratification.Density(4) > ReservoirStratification.Density(0));
        Assert.True(ReservoirStratification.Density(4) > ReservoirStratification.Density(8));
        Assert.InRange(ReservoirStratification.Density(30), 995.60, 995.70);   // table: 995.65 kg/m³
        Assert.True(ReservoirStratification.Density(27) > ReservoirStratification.Density(31));
        Assert.Equal(0, ReservoirStratification.SchmidtStability(Enumerable.Repeat(28.0, 40).ToArray(), 0.5), 6);
    }

    [Fact]
    public void Calm_weather_stratifies_the_column_and_the_hypolimnion_turns_anoxic()
    {
        var hours = ReservoirStratification.Simulate(new ReservoirSettings(), Calm(new DateTime(2026, 1, 1), 20), 28);
        var noonDay19 = hours[19 * 24 + 12];
        Assert.True(noonDay19.SurfaceTemp - noonDay19.BottomTemp > 3, $"ΔT {noonDay19.SurfaceTemp - noonDay19.BottomTemp:0.0}");
        Assert.True(noonDay19.SchmidtStability > 100);
        Assert.True(noonDay19.AnoxicThicknessM > 8, $"anoxic {noonDay19.AnoxicThicknessM} m");
        Assert.True(noonDay19.CageDo > 4, "the cage zone stays oxygenated while stratified");
        Assert.InRange(hours.Max(h => h.SurfaceTemp), 31, 36);
        Assert.Empty(ReservoirStratification.DetectUpwelling(hours));
        // night convection deepens the surface mixed layer
        Assert.True(hours[10 * 24 + 6].MixedLayerM > hours[10 * 24 + 14].MixedLayerM);
    }

    [Fact]
    public void A_cold_windy_rainy_spell_overturns_the_column_and_causes_umbalan_in_the_cages()
    {
        var t0 = new DateTime(2026, 1, 1);
        var w = Calm(t0, 20); w.AddRange(Storm(t0.AddDays(20), 3)); w.AddRange(Calm(t0.AddDays(23), 2));
        var r = new ReservoirSettings();
        var hours = ReservoirStratification.Simulate(r, w, 28);
        var e = Assert.Single(ReservoirStratification.DetectUpwelling(hours));
        Assert.InRange(e.Start, t0.AddDays(20), t0.AddDays(23));
        Assert.True(e.MinCageDo < 2, $"cage DO {e.MinCageDo}");
        Assert.True(e.MixedLayerM >= 10);
        Assert.True(e.AnoxicBeforeM > 8);

        // a shallow cage site (5 m) with little hypolimnion volume does not crash as hard
        var shallow = ReservoirStratification.Simulate(r with { DepthM = 5 }, w, 28);
        Assert.True(shallow.Skip(20 * 24).Min(h => h.CageDo) > hours.Skip(20 * 24).Min(h => h.CageDo));

        // the risk index from the stratified state before the storm is high when the forecast is stormy, low when calm
        var before = hours[20 * 24 - 1];
        var stormy = ReservoirStratification.Assess(r, Storm(t0.AddDays(20), 3), before.TempC, before.Do);
        var calm = ReservoirStratification.Assess(r, Calm(t0.AddDays(20), 3), before.TempC, before.Do);
        Assert.True(stormy.RiskIndex >= 60, $"stormy risk {stormy.RiskIndex}");
        Assert.True(calm.RiskIndex < stormy.RiskIndex);
        Assert.Contains("umbalan", stormy.Advice.Id);
    }
}
