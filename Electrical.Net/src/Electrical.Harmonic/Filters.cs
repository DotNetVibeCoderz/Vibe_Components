using System.Numerics;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Harmonic;

/// <summary>Single-tuned (series R–L–C) shunt harmonic filter. Ohms are per phase (wye equivalent).</summary>
public sealed record SingleTunedFilter(
    double SystemKv, double FrequencyHz, double TunedOrder, double QualityFactor, double EffectiveMvar,
    double CapacitorReactanceOhm, double ReactorReactanceOhm, double ResistanceOhm)
{
    /// <summary>
    /// Designs a filter delivering <paramref name="effectiveMvar"/> at the fundamental and tuned to <paramref name="tunedOrder"/>
    /// (typically 3–10 % below the target harmonic, e.g. 4.7 for the 5th):
    /// X_eff = kV²/Q, X_C = X_eff·h²/(h² − 1), X_L = X_C/h², R = h·X_L/Q.
    /// </summary>
    public static SingleTunedFilter Design(double systemKv, double effectiveMvar, double tunedOrder, double qualityFactor = 30, double frequencyHz = 50)
    {
        double xeff = systemKv * systemKv / effectiveMvar;
        double h2 = tunedOrder * tunedOrder;
        double xc = xeff * h2 / (h2 - 1);
        double xl = xc / h2;
        return new SingleTunedFilter(systemKv, frequencyHz, tunedOrder, qualityFactor, effectiveMvar, xc, xl, tunedOrder * xl / qualityFactor);
    }

    /// <summary>Capacitance per phase [µF].</summary>
    public double CapacitanceMicroF => 1e6 / (2 * Math.PI * FrequencyHz * CapacitorReactanceOhm);
    /// <summary>Inductance per phase [mH].</summary>
    public double InductanceMilliH => 1e3 * ReactorReactanceOhm / (2 * Math.PI * FrequencyHz);
    /// <summary>Capacitor voltage rise at the fundamental V_C/V = h²/(h² − 1).</summary>
    public double CapacitorVoltageRise => TunedOrder * TunedOrder / (TunedOrder * TunedOrder - 1);
    /// <summary>Capacitor three-phase rating at its (raised) operating voltage [Mvar].</summary>
    public double CapacitorRatingMvar => EffectiveMvar * CapacitorVoltageRise;

    /// <summary>Filter impedance at harmonic order h [Ω]: R + j(h·X_L − X_C/h).</summary>
    public Complex Impedance(double h) => new(ResistanceOhm, h * ReactorReactanceOhm - CapacitorReactanceOhm / h);

    /// <summary>Equivalent shunt at the fundamental to add to a bus (<see cref="Bus.Bs"/>, Mvar at 1 pu).</summary>
    public double FundamentalMvar => SystemKv * SystemKv / (CapacitorReactanceOhm - ReactorReactanceOhm);

    /// <summary>
    /// Adds the filter to <paramref name="network"/> as an auxiliary bus <paramref name="filterBusId"/> connected to <paramref name="busId"/>
    /// through the reactor (R + jX_L branch) with the capacitor as a shunt at the auxiliary bus. The harmonic Y-bus then reproduces
    /// R + j(h·X_L − X_C/h) exactly, and the power flow sees the fundamental compensation.
    /// </summary>
    public void AddTo(PowerNetwork network, int busId, int filterBusId)
    {
        var bus = network.GetBus(busId);
        double zb = bus.BaseKv * bus.BaseKv / network.BaseMva;
        network.Buses.Add(new Bus { Id = filterBusId, BaseKv = bus.BaseKv, Name = $"Filter h={TunedOrder:0.##}", Bs = bus.BaseKv * bus.BaseKv / CapacitorReactanceOhm });
        network.Branches.Add(new Branch { From = busId, To = filterBusId, R = ResistanceOhm / zb, X = ReactorReactanceOhm / zb, Name = $"Filter reactor h={TunedOrder:0.##}" });
    }

    /// <summary>Fraction of a harmonic current source current that flows into the filter, given the network Thevenin impedance Z_s(h) [Ω].</summary>
    public double CurrentDivision(double h, Complex sourceImpedanceOhm)
    {
        var zf = Impedance(h);
        return (sourceImpedanceOhm / (sourceImpedanceOhm + zf)).Magnitude;
    }
}

/// <summary>Harmonic/power-quality service resolvable from DI.</summary>
public interface IHarmonicService
{
    HarmonicSpectrum Analyze(double[] samples, double sampleRateHz, double fundamentalHz, int maxOrder = 50);
    HarmonicLoadFlowResult LoadFlow(PowerNetwork network, IEnumerable<HarmonicInjection> sources, HarmonicOptions? options = null);
    IReadOnlyList<FrequencyScanPoint> FrequencyScan(PowerNetwork network, int busId, double hMax = 50);
}

internal sealed class HarmonicService : IHarmonicService
{
    public HarmonicSpectrum Analyze(double[] samples, double sampleRateHz, double fundamentalHz, int maxOrder = 50) =>
        HarmonicSpectrum.FromSamples(samples, sampleRateHz, fundamentalHz, maxOrder);
    public HarmonicLoadFlowResult LoadFlow(PowerNetwork network, IEnumerable<HarmonicInjection> sources, HarmonicOptions? options = null) =>
        HarmonicLoadFlow.Run(network, sources, options);
    public IReadOnlyList<FrequencyScanPoint> FrequencyScan(PowerNetwork network, int busId, double hMax = 50) =>
        Harmonic.FrequencyScan.DrivingPoint(network, busId, 1, hMax);
}

public static class HarmonicBuilderExtensions
{
    /// <summary>Registers <see cref="IHarmonicService"/>.</summary>
    public static ElectricalBuilder AddHarmonic(this ElectricalBuilder b)
    {
        if (b.TryAddModule("Harmonic")) b.Services.TryAddSingleton<IHarmonicService, HarmonicService>();
        return b;
    }
}
