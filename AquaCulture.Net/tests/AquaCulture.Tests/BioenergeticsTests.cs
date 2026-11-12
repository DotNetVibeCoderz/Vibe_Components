using AquaCulture.Growth;

namespace AquaCulture.Tests;

public class BioenergeticsTests
{
    [Fact]
    public void Kitchell_temperature_function_peaks_at_the_optimum_and_vanishes_at_the_maximum()
    {
        var b = BioenergeticsParameters.Tilapia;
        Assert.Equal(1, Bioenergetics.TemperatureFactor(b.CTO, b.CTO, b.CTM, b.CQ), 9);
        Assert.Equal(0, Bioenergetics.TemperatureFactor(b.CTM, b.CTO, b.CTM, b.CQ));
        double prev = 0;
        for (double t = 14; t <= b.CTO; t += 1) { double f = Bioenergetics.TemperatureFactor(t, b.CTO, b.CTM, b.CQ); Assert.True(f > prev); prev = f; }
        Assert.True(Bioenergetics.TemperatureFactor(36, b.CTO, b.CTM, b.CQ) < Bioenergetics.TemperatureFactor(33, b.CTO, b.CTM, b.CQ));
    }

    [Fact]
    public void Daily_budget_closes_and_indicative_sets_give_plausible_growth()
    {
        foreach (var (b, w, t, adgLo, adgHi) in new[] { (BioenergeticsParameters.Tilapia, 100.0, 28.0, 1.0, 2.5), (BioenergeticsParameters.Vaname, 10.0, 30.0, 0.15, 0.45) })
        {
            var d = Bioenergetics.Day(b, 0, w, t, 0.7);
            double closed = d.EnergyInJ - d.EgestionJ - d.ExcretionJ - d.SdaJ - d.RespirationJ - d.GainG * b.PredatorEnergy;
            Assert.Equal(0, closed, 6);                                                  // energy balance
            Assert.InRange(d.GainG, adgLo, adgHi);
            Assert.InRange(d.Fcr, 0.8, 2.0);
            Assert.True(Bioenergetics.Day(b, 0, w, t, 0.2).GainG < 0.3 * d.GainG, "low ration barely covers maintenance");
            Assert.True(Bioenergetics.Day(b, 0, w, t, 0).GainG < 0, "starvation loses weight");
        }
    }

    [Fact]
    public void Fitted_p_reproduces_an_observed_growth_and_tilapia_reaches_market_size()
    {
        var b = BioenergeticsParameters.Tilapia;
        var temps = Enumerable.Range(0, 120).Select(d => 28 + 1.5 * Math.Sin(d / 19.0)).ToList();
        double target = Bioenergetics.FinalWeight(b, 20, temps, 0.63);
        double p = Bioenergetics.FitP(b, 20, target, temps);
        Assert.Equal(0.63, p, 4);
        var sim = Bioenergetics.Simulate(b, 20, Enumerable.Repeat(29.0, 120).ToList(), 0.7);
        Assert.InRange(sim[^1].WeightG + sim[^1].GainG, 160, 260);
        Assert.InRange(Bioenergetics.Totals(sim).Fcr, 1.2, 1.8);
        Assert.True(double.IsNaN(Bioenergetics.FitP(b, 20, 1e7, temps)), "beyond twice the maximum ration: unreachable");
    }

    private static readonly DebParameters Illustrative = new()
    {
        Name = "illustrative fish", PAm = 60, V = 0.03, Kappa = 0.8, PM = 25, EG = 5200, ShapeCoefficient = 0.3, ArrheniusK = 8000, ReferenceC = 20,
    };

    [Fact]
    public void Deb_growth_at_constant_food_follows_von_Bertalanffy()
    {
        var p = Illustrative;
        double f = 0.8;
        var start = DebModel.Start(p, 0.2, f);
        int days = 2000;
        var states = DebModel.Simulate(p, start, Enumerable.Repeat(f, days).ToList(), Enumerable.Repeat(p.ReferenceC, days).ToList());
        double lInf = f * p.Lm, rB = DebModel.VonBertalanffyRate(p, f);
        foreach (int t in new[] { 100, 400, 1000, 2000 })
        {
            double expected = lInf - (lInf - 0.2) * Math.Exp(-rB * t);
            Assert.Equal(expected, states[t].StructuralLength, 2);
        }
        // warmer water speeds growth (Arrhenius), starvation shrinks nothing within a day but drains reserve
        var warm = DebModel.Simulate(p, start, Enumerable.Repeat(f, 200).ToList(), Enumerable.Repeat(28.0, 200).ToList());
        Assert.True(warm[200].StructuralLength > states[200].StructuralLength);
        var hungry = DebModel.Simulate(p, states[400], Enumerable.Repeat(0.0, 10).ToList(), Enumerable.Repeat(20.0, 10).ToList());
        Assert.True(hungry[10].ReserveJ < hungry[0].ReserveJ);
        var (len, w) = DebModel.Size(p, states[1000]);
        Assert.True(len > 0 && w > 0);
    }

    [Fact]
    public void Shrimp_moult_cycle_is_size_and_temperature_dependent_and_stepwise()
    {
        var m = new ShrimpMoultModel();
        Assert.True(m.IntermoultDays(20, 28) > m.IntermoultDays(1, 28));
        Assert.True(m.IntermoultDays(10, 31) < m.IntermoultDays(10, 26));
        Assert.InRange(m.IntermoultDays(10, 28), 9, 12);

        var smooth = Enumerable.Range(0, 60).Select(d => 2 * Math.Exp(0.04 * d)).ToList();
        var cal = m.Calendar(smooth, Enumerable.Repeat(29.0, 60).ToList());
        int moults = cal.Count(c => c.Ecdysis);
        Assert.InRange(moults, 5, 12);
        for (int i = 1; i < cal.Count; i++)
            if (!cal[i].Ecdysis) Assert.Equal(cal[i - 1].WeightG, cal[i].WeightG);     // no growth between moults
        Assert.All(cal.Where(c => c.Ecdysis), c => Assert.Equal(MoultStage.A, c.Stage));
        Assert.True(cal.Average(c => c.FeedFactor) is > 0.75 and < 1);

        var synced = m.PondMoulting(30, 12, 29, synchrony: 0.7, triggerDay: 0);
        var spread = m.PondMoulting(30, 12, 29, synchrony: 0);
        Assert.True(synced.Max(d => d.MoultingShare) > 3 * spread.Max(d => d.MoultingShare), "a synchronising event concentrates moulting");
        Assert.True(synced.Min(d => d.FeedFactor) < spread.Min(d => d.FeedFactor), "mass moulting lowers pond feeding");
    }
}
