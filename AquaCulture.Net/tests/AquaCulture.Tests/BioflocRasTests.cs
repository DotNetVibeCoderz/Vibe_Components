using AquaCulture.Hydrodynamic;

namespace AquaCulture.Tests;

public class BioflocRasTests
{
    private static double Feed(int d) => 5 + 0.25 * d;

    [Fact]
    public void Carbon_dosing_follows_Ebeling_stoichiometry_and_the_cn_ratio()
    {
        // 100 kg of 35 % protein feed releases 100 × 0.35 × 0.092 = 3.22 kg TAN-N; 15.17 kg sugar per kg N; molasses is half sugar
        Assert.Equal(3.22 * 15.17 / 0.5, BioflocModel.CarbonSourceForFullAssimilation(100, 0.35), 6);
        Assert.Equal(8.0, BioflocModel.CnRatio(100, 0, 0.35), 1);
        double molasses = BioflocModel.CarbonSourceForCn(100, 0.35, 15);
        Assert.Equal(15, BioflocModel.CnRatio(100, molasses, 0.35), 6);
        Assert.Equal(0, BioflocModel.CarbonSourceForCn(100, 0.35, 5));                  // already above target
    }

    [Fact]
    public void A_new_pond_shows_the_start_up_nitrite_peak_that_carbon_dosing_suppresses()
    {
        var p = new BioflocParameters { VolumeM3 = 1000 };
        var clear = BioflocModel.Simulate(p, 60, Feed, (_, _) => 0);
        var biofloc = BioflocModel.Simulate(p, 60, Feed, (d, _) => BioflocModel.CarbonSourceForCn(Feed(d), 0.35, 15));
        var mature = BioflocModel.Simulate(p, 60, Feed, (_, _) => 0, maturity0: 1);

        var peak = clear.MaxBy(x => x.Nitrite)!;
        Assert.InRange(peak.Day, 8, 35);
        Assert.True(peak.Nitrite > 2 * clear[^1].Nitrite, "the nitrite peak passes once NOB mature");
        Assert.True(clear.Max(x => x.Nitrite) > 2 * mature.Max(x => x.Nitrite), "an established system has no peak");
        Assert.True(biofloc.Max(x => x.Tan) < clear.Max(x => x.Tan), "heterotrophs take TAN up");
        Assert.True(biofloc.Max(x => x.Nitrite) < clear.Max(x => x.Nitrite));
        Assert.True(biofloc[^1].Nitrate < mature[^1].Nitrate, "less N goes through nitrification");
        Assert.True(biofloc[^1].TssMgL > clear[^1].TssMgL, "bacterial biomass builds the floc");
        Assert.Equal(15, biofloc[^1].CnRatio, 1);
    }

    [Fact]
    public void Alkalinity_runs_out_without_dosing_and_bicarbonate_keeps_it()
    {
        var p = new BioflocParameters { VolumeM3 = 1000 };
        var noDose = BioflocModel.Simulate(p, 60, Feed, (_, _) => 0, maturity0: 1);
        var dosed = BioflocModel.Simulate(p with { BicarbonateTargetMgL = 140 }, 60, Feed, (_, _) => 0, maturity0: 1);
        Assert.True(noDose[^1].AlkalinityMgL < 30);
        Assert.All(noDose, d => Assert.True(d.AlkalinityMgL >= 0));
        Assert.True(dosed.Min(d => d.AlkalinityMgL) > 100);
        Assert.True(dosed.Sum(d => d.BicarbonateKg) > 50);
        Assert.True(dosed[^1].Tan <= noDose[^1].Tan, "nitrification works better with alkalinity");
        Assert.True(BioflocModel.Simulate(p with { SludgeRemovalPerDay = 0.01 }, 60, Feed, (_, _) => 0)[^1].FlocVolumeMlL >
                    BioflocModel.Simulate(p with { SludgeRemovalPerDay = 0.15 }, 60, Feed, (_, _) => 0)[^1].FlocVolumeMlL);
    }

    [Fact]
    public void Ras_components_behave_like_their_design_equations()
    {
        Assert.Equal(0.5, new DrumFilter(MeshMicron: 60, MedianParticleMicron: 60).Efficiency, 6);
        Assert.True(new DrumFilter(MeshMicron: 30).Efficiency > new DrumFilter(MeshMicron: 90).Efficiency);
        Assert.Equal(1 - Math.Exp(-1.5), new Degasser(10, 1).Efficiency, 9);
        Assert.True(new Degasser(5, 1).Efficiency < new Degasser(10, 1.5).Efficiency);

        var mbbr = new MbbrReactor(4);
        Assert.Equal(1000, mbbr.MediaAreaM2);
        double t = mbbr.SteadyStateTan(600, 20);
        Assert.Equal(600, mbbr.TanRemovalGPerDay(t, 20), 6);
        Assert.True(double.IsNaN(mbbr.SteadyStateTan(5000, 20)));
        double v = MbbrReactor.RequiredVolumeM3(1840, 1.0, 28);
        Assert.Equal(1.0, new MbbrReactor(v).SteadyStateTan(1840, 28), 6);

        var small = RasLoop.SteadyState(50, 0.4, 60, new DrumFilter(), new MbbrReactor(6), new Degasser(), 28);
        var big = RasLoop.SteadyState(50, 0.4, 120, new DrumFilter(MeshMicron: 40), new MbbrReactor(12), new Degasser(10, 1.5), 28);
        Assert.Equal(1840, small.TanLoadGPerDay);
        Assert.True(big.TanMgL < small.TanMgL && big.Co2MgL < small.Co2MgL && big.TssMgL < small.TssMgL);
        Assert.True(RasLoop.SteadyState(50, 0.4, 60, new DrumFilter(), new MbbrReactor(0.5), new Degasser(), 28).BiofilterOverloaded);
    }
}
