using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Renewable;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class RenewableTests
{
    [Fact]
    public void SolarPosition_MatchesNrelSpaExample()
    {
        // NREL SPA report (Reda & Andreas 2008) example: 2003-10-17 12:30:30 MST (UTC−7), 39.742476 N, 105.1786 W
        // → topocentric zenith 50.11162°, azimuth 194.34024°.
        var t = new DateTimeOffset(2003, 10, 17, 12, 30, 30, TimeSpan.FromHours(-7));
        var s = SolarPosition.Compute(t, 39.742476, -105.1786);
        Assert.Equal(50.11162, s.ZenithDeg, 0.05);
        Assert.Equal(194.34024, s.AzimuthDeg, 0.05);
        var sp = SolarPosition.Spencer(t, 39.742476, -105.1786);
        Assert.Equal(50.11, sp.ZenithDeg, 0.6);
    }

    [Fact]
    public void Irradiance_TranspositionAndTemperature()
    {
        var sun = new SunPosition(30, 180, 0, 0, 0);
        var irr = new IrradianceComponents(800, 700, 800 - 700 * Math.Cos(Math.PI / 6));
        var flat = Irradiance.PlaneOfArray(irr, sun, 0, 180);
        Assert.Equal(800, flat.Global, 6);                           // horizontal plane returns GHI
        var tilted = Irradiance.PlaneOfArray(irr, sun, 30, 180);
        Assert.Equal(0, tilted.AngleOfIncidenceDeg, 6);              // panel normal points at the sun
        Assert.Equal(700 + irr.Dhi * (1 + Math.Cos(Math.PI / 6)) / 2 + 800 * 0.2 * (1 - Math.Cos(Math.PI / 6)) / 2, tilted.Global, 6);
        Assert.Equal(45, CellTemperature.Noct(20, 800, 45), 12);     // NOCT definition point
        Assert.Equal(25 + 1000 / (25 + 6.84), CellTemperature.Faiman(25, 1000, 1), 12);
        var cs = Irradiance.IneichenClearSky(30, 172, 3);
        Assert.InRange(cs.Ghi, 750, 950);
        var erbs = Irradiance.Erbs(cs.Ghi, 30, 172);
        Assert.Equal(cs.Ghi, erbs.Dhi + erbs.Dni * Math.Cos(Math.PI / 6), 6);
    }

    [Fact]
    public void SingleDiode_KC200GT_MatchesVillalva()
    {
        // Villalva, Gazoli & Filho, IEEE Trans. Power Electron. 24(5), 2009, Table II: Rs = 0.221 Ω, Rp = 415.405 Ω, Ipv = 8.214 A, I0 = 9.825e-8 A.
        var m = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT, 1.3);
        Assert.Equal(0.221, m.Rs, 0.002);
        Assert.Equal(415.4, m.Rp, 15.0);
        var exact = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT, 1.3, pmaxToleranceW: 0);
        Assert.Equal(200.143, exact.MaximumPowerPoint().Power, 0.001);
        Assert.Equal(26.3, exact.MaximumPowerPoint().Voltage, 0.01);
        Assert.Equal(8.214, m.IpvStc, 0.002);
        Assert.Equal(9.825e-8, m.I0Stc, 0.05e-8);
        var mpp = m.MaximumPowerPoint();
        Assert.Equal(200.143, mpp.Power, 0.2);
        Assert.Equal(26.3, mpp.Voltage, 0.1);
        Assert.Equal(8.21, m.Current(0), 0.01);
        Assert.Equal(32.9, m.OpenCircuitVoltage(), 0.05);
        // temperature: Voc(75 °C) ≈ 32.9 − 0.123·50 = 26.75 V
        Assert.Equal(26.75, m.OpenCircuitVoltage(1000, 75), 0.3);
        var curve = m.IvCurve(600, 25, 50);
        Assert.Equal(50, curve.Length);
        Assert.True(curve.Max(p => p.Power) < mpp.Power * 0.65);
    }

    [Fact]
    public void Pvwatts_Inverter_And_Mppt()
    {
        // PVWatts v5: at ζ = 1 the efficiency equals the nominal efficiency (η_ref normalization).
        Assert.Equal(0.96 * 1000 / 0.96, PvWatts.InverterAc(1000 / 0.96, 1000), 9);
        Assert.Equal(1000, PvWatts.InverterAc(2000, 1000)); // clipping
        Assert.Equal(1000 * 0.8 * (1 - 0.0047 * 20), PvWatts.DcPower(800, 45, 1000), 9);
        double cec = InverterEfficiencyCurve.Typical.CecWeighted();
        Assert.InRange(cec, 0.97, 0.98);
        Assert.True(InverterEfficiencyCurve.Typical.EuroWeighted() < cec);
        var sandia = new SandiaInverter(Paco: 250, Pdco: 259.5, Vdco: 40, Pso: 1.8, C0: -4e-5);
        Assert.InRange(sandia.AcPower(200, 40), 190, 200);

        var module = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT);
        var g = Enumerable.Range(0, 200).Select(k => k < 100 ? 1000.0 : 700.0).ToArray();
        var trace = Mppt.PerturbAndObserve(module, g, 25, stepVolts: 0.2);
        Assert.True(trace.TrackingEfficiency > 0.97, $"{trace.TrackingEfficiency:P2}");
    }

    [Fact]
    public void Wind_WeibullYieldAndProfiles()
    {
        var w = new WeibullDistribution(2, 8);
        Assert.Equal(8 * 0.886226925, w.Mean, 6); // Γ(1.5) = √π/2
        // Rayleigh energy pattern factor 6/π (Masters, Renewable and Efficient Electric Power Systems, eq. 6.48)
        var r = WeibullDistribution.Rayleigh(7);
        Assert.Equal(0.5 * 1.225 * 6 / Math.PI * 343, r.PowerDensity(), 6);
        Assert.Equal(1.2250, AirDensity.FromTemperaturePressure(15, 101325), 3);
        Assert.Equal(Math.Pow(8, 1.0 / 7), WindShear.PowerLaw(1, 10, 80), 12);
        Assert.Equal(Math.Log(80 / 0.03) / Math.Log(10 / 0.03), WindShear.LogLaw(1, 10, 80), 12);

        var t = WindTurbine.Generic2MW;
        var aep = WindEnergy.AnnualEnergyProduction(t, WeibullDistribution.Rayleigh(7.5));
        Assert.InRange(aep.CapacityFactor, 0.25, 0.45);
        Assert.Equal(aep.CapacityFactor * 2000 * 8760 / 1000, aep.AepMwh, 6);
        // thinner air (1500 m) yields less energy
        Assert.True(WindEnergy.AnnualEnergyProduction(t, WeibullDistribution.Rayleigh(7.5), AirDensity.AtAltitude(1500)).AepMwh < aep.AepMwh);
        var table = WindTurbine.FromTable([3, 5, 8, 11, 12], [0, 150, 700, 1900, 2000], 25);
        Assert.Equal(425, table.Power(6.5), 9);
        Assert.Equal(0, table.Power(25));
    }

    [Fact]
    public void Battery_EfficiencyLimitsAndDegradation()
    {
        var b = new BatteryStorage(10, 5, initialSoc: 0.5) { SocMin = 0.1, SocMax = 0.9 };
        Assert.Equal(-4, b.Step(-4, 1), 12);                     // charge 4 MWh from grid
        Assert.Equal(0.5 + 4 * 0.95 / 10, b.Soc, 12);
        double p = b.Step(-10, 1);                               // limited by SoC (and 5 MW)
        Assert.Equal(-(0.9 - 0.88) * 10 / 0.95, p, 9);
        Assert.Equal(0.9, b.Soc, 9);
        double delivered = 0;
        for (int i = 0; i < 10; i++) delivered += b.Step(5, 1);
        Assert.Equal(0.1, b.Soc, 9);
        Assert.Equal((0.9 - 0.1) * 10 * 0.95, delivered, 3); // slightly less: capacity fade after the first half cycle
        b.FinalizeCycles();
        Assert.True(b.CycleDamage > 0 && b.StateOfHealth < 1);
        Assert.True(b.EquivalentFullCycles > 0.5);
        Assert.Equal(5000 * Math.Pow(0.5, -1.3), b.CycleLife(0.5), 6);
        Assert.Equal(2.5, BatteryStorage.FromCRate(10, 0.25).MaxDischargeMw);
    }

    [Fact]
    public void GridCodes_Ieee1547Defaults()
    {
        var vv = VoltVarCurve.Ieee1547CategoryB;
        Assert.Equal(0.44, vv.Q(0.90), 12);
        Assert.Equal(0.22, vv.Q(0.95), 12);
        Assert.Equal(0, vv.Q(1.00), 12);
        Assert.Equal(-0.22, vv.Q(1.05), 12);
        Assert.Equal(-0.44, vv.Q(1.10), 12);
        Assert.Equal(1.0, VoltWattCurve.Ieee1547.PLimit(1.05), 12);
        Assert.Equal(0.6, VoltWattCurve.Ieee1547.PLimit(1.08), 12);
        var fw = FrequencyDroop.Ieee1547;
        Assert.Equal(1.0, fw.Power(60.03, 1.0), 12);                       // inside deadband
        Assert.Equal(1.0 - 0.5 / 3.0, fw.Power(60.536, 1.0), 9);          // 0.5 Hz beyond db at 5 % droop → −16.7 %
        Assert.Equal(0.5 + 0.5 / 3.0, fw.Power(59.464, 0.5), 9);
    }

    [Fact]
    public void HostingCapacity_Case33_EndOfFeederIsVoltageLimited()
    {
        var net = TestCases.Case33BW();
        var end = HostingCapacity.Analyze(net, 18, 0.25, 6);
        var near = HostingCapacity.Analyze(net, 3, 0.5, 10);
        Assert.Equal("Overvoltage", end.LimitingFactor);
        Assert.InRange(end.HostingCapacityMw, 1.0, 3.5);
        Assert.True(near.HostingCapacityMw > 2 * end.HostingCapacityMw);
        Assert.True(end.MaxVoltagePu.Zip(end.MaxVoltagePu.Skip(1)).Skip(3).All(p => p.Second >= p.First));

        var sp = new ServiceCollection().AddElectrical(b => b.AddRenewable()).BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IRenewableService>());
    }
}
