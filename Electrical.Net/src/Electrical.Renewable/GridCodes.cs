using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Renewable;

/// <summary>
/// Volt-VAR characteristic (piecewise linear through four points). Voltages in pu; reactive power in pu of the rated apparent
/// power, positive = injection (capacitive / over-excited).
/// </summary>
public sealed record VoltVarCurve(double V1, double Q1, double V2, double Q2, double V3, double Q3, double V4, double Q4)
{
    /// <summary>IEEE 1547-2018 Category B default (Table 8): (0.92, +0.44), (0.98, 0), (1.02, 0), (1.08, −0.44).</summary>
    public static VoltVarCurve Ieee1547CategoryB { get; } = new(0.92, 0.44, 0.98, 0, 1.02, 0, 1.08, -0.44);

    /// <summary>IEEE 1547-2018 Category A default: (0.90, +0.25), (1.00, 0), (1.00, 0), (1.10, −0.25).</summary>
    public static VoltVarCurve Ieee1547CategoryA { get; } = new(0.90, 0.25, 1.0, 0, 1.0, 0, 1.10, -0.25);

    /// <summary>Reactive power set-point [pu of rated VA] at voltage <paramref name="v"/> [pu].</summary>
    public double Q(double v) => Piecewise(v, [V1, V2, V3, V4], [Q1, Q2, Q3, Q4]);

    internal static double Piecewise(double x, double[] xs, double[] ys)
    {
        if (x <= xs[0]) return ys[0];
        for (int i = 1; i < xs.Length; i++)
            if (x <= xs[i])
                return xs[i] == xs[i - 1] ? ys[i] : ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[^1];
    }
}

/// <summary>Volt-Watt characteristic: active power limit [pu of rated] falls linearly from P1 at V1 to P2 at V2.</summary>
public sealed record VoltWattCurve(double V1, double P1, double V2, double P2)
{
    /// <summary>IEEE 1547-2018 default (Table 10): V1 = 1.06 pu, P1 = 1.0; V2 = 1.10 pu, P2 = 0.2 (DER that cannot absorb power).</summary>
    public static VoltWattCurve Ieee1547 { get; } = new(1.06, 1.0, 1.10, 0.2);

    /// <summary>Maximum active power [pu of rated] at voltage <paramref name="v"/> [pu].</summary>
    public double PLimit(double v) => VoltVarCurve.Piecewise(v, [V1, V2], [P1, P2]);
}

/// <summary>
/// Frequency-droop (frequency-Watt) response. Over-frequency: ΔP = −(f − (f_n + db))/(f_n·k)·P_rated; under-frequency: increase toward
/// available power. Defaults are the IEEE 1547-2018 Category I–III settings: db = 0.036 Hz, k = 5 %, open-loop response.
/// </summary>
public sealed record FrequencyDroop(double NominalHz = 60, double DeadbandHz = 0.036, double Droop = 0.05)
{
    public static FrequencyDroop Ieee1547 { get; } = new();

    /// <summary>
    /// Active power [pu of rated] after the droop response, given the pre-disturbance output, available (e.g. MPPT) power and minimum power, all in pu.
    /// </summary>
    public double Power(double frequencyHz, double prePu, double availablePu = 1, double minPu = 0)
    {
        double fn = NominalHz;
        if (frequencyHz > fn + DeadbandHz)
            return Math.Max(minPu, prePu - (frequencyHz - (fn + DeadbandHz)) / (fn * Droop));
        if (frequencyHz < fn - DeadbandHz)
            return Math.Min(availablePu, prePu + ((fn - DeadbandHz) - frequencyHz) / (fn * Droop));
        return prePu;
    }
}

/// <summary>Result of a hosting-capacity sweep at one bus.</summary>
/// <param name="BusId">Bus where PV was added.</param>
/// <param name="HostingCapacityMw">Largest PV injection [MW] without violation.</param>
/// <param name="LimitingFactor">"Overvoltage", "Thermal", "NonConvergence" or "None" (no violation up to the search limit).</param>
/// <param name="PvMw">Injections evaluated [MW].</param>
/// <param name="MaxVoltagePu">Maximum bus voltage at each injection [pu].</param>
/// <param name="MaxLoadingPercent">Maximum branch loading at each injection [% of rating].</param>
public sealed record HostingCapacityResult(int BusId, double HostingCapacityMw, string LimitingFactor, double[] PvMw, double[] MaxVoltagePu, double[] MaxLoadingPercent);

/// <summary>
/// Hosting-capacity analysis: increases a unity-power-factor PV injection at a bus with AC power flow until the maximum voltage
/// exceeds <c>vmax</c> or a branch exceeds its thermal rating, then refines the limit by bisection.
/// </summary>
public static class HostingCapacity
{
    /// <param name="vmaxPu">Overvoltage limit [pu] (ANSI C84.1 Range A upper limit 1.05 pu).</param>
    /// <param name="defaultRatingMva">Rating [MVA] used for branches without RateA (0 = ignore unrated branches).</param>
    /// <param name="maxMw">Upper end of the search [MW].</param>
    /// <param name="stepMw">Coarse sweep step [MW].</param>
    public static HostingCapacityResult Analyze(PowerNetwork net, int busId, double stepMw, double maxMw, double vmaxPu = 1.05,
        double loadingLimitPercent = 100, double defaultRatingMva = 0, double powerFactor = 1.0)
    {
        var work = net.Clone();
        if (defaultRatingMva > 0) foreach (var b in work.Branches) if (b.RateA <= 0) b.RateA = defaultRatingMva;
        var pv = new Generator { Bus = busId, Name = "HC-PV", Kind = GeneratorKind.Solar, P = 0, Pmax = maxMw };
        work.Generators.Add(pv);
        double tanPhi = Math.Tan(Math.Acos(Math.Clamp(powerFactor, 0.01, 1)));
        var ps = new List<double>(); var vs = new List<double>(); var ls = new List<double>();
        var opts = new PowerFlowOptions { FlatStart = true };

        string Check(double p, bool record)
        {
            pv.P = p; pv.Q = -p * tanPhi; // absorbing reactive power for pf < 1
            PowerFlowResult r;
            try { r = PowerFlowSolver.Solve(work, opts); }
            catch (Exception) { return "NonConvergence"; }
            if (!r.Converged) return "NonConvergence";
            double vmax = r.Buses.Max(b => b.Vm);
            double load = r.Branches.Count == 0 ? 0 : r.Branches.Max(b => b.LoadingPercent);
            if (record) { ps.Add(p); vs.Add(vmax); ls.Add(load); }
            if (vmax > vmaxPu + 1e-9) return "Overvoltage";
            if (load > loadingLimitPercent + 1e-9) return "Thermal";
            return "None";
        }

        double ok = 0, bad = double.NaN;
        string factor = "None";
        for (double p = 0; p <= maxMw + 1e-12; p += stepMw)
        {
            var f = Check(p, true);
            if (f != "None") { bad = p; factor = f; break; }
            ok = p;
        }
        if (!double.IsNaN(bad))
        {
            double lo = ok, hi = bad;
            for (int it = 0; it < 30 && hi - lo > 1e-4 * Math.Max(1, stepMw); it++)
            {
                double mid = 0.5 * (lo + hi);
                var f = Check(mid, false);
                if (f == "None") lo = mid; else { hi = mid; factor = f; }
            }
            ok = lo;
        }
        return new HostingCapacityResult(busId, ok, factor, [.. ps], [.. vs], [.. ls]);
    }

    /// <summary>Hosting capacity of every PQ bus (coarse sweep + bisection per bus).</summary>
    public static IReadOnlyList<HostingCapacityResult> AnalyzeAll(PowerNetwork net, double stepMw, double maxMw, double vmaxPu = 1.05,
        double loadingLimitPercent = 100, double defaultRatingMva = 0) =>
        net.Buses.Where(b => b.Type == BusType.PQ).AsParallel().AsOrdered()
            .Select(b => Analyze(net, b.Id, stepMw, maxMw, vmaxPu, loadingLimitPercent, defaultRatingMva)).ToList();
}

/// <summary>DI facade for renewable models.</summary>
public interface IRenewableService
{
    SunPosition SunPosition(DateTimeOffset time, double latitudeDeg, double longitudeDeg);
    AnnualEnergy WindAnnualEnergy(WindTurbine turbine, WeibullDistribution wind);
    HostingCapacityResult HostingCapacity(PowerNetwork net, int busId, double stepMw, double maxMw);
}

internal sealed class RenewableService : IRenewableService
{
    public SunPosition SunPosition(DateTimeOffset time, double latitudeDeg, double longitudeDeg) => SolarPosition.Compute(time, latitudeDeg, longitudeDeg);
    public AnnualEnergy WindAnnualEnergy(WindTurbine turbine, WeibullDistribution wind) => WindEnergy.AnnualEnergyProduction(turbine, wind);
    public HostingCapacityResult HostingCapacity(PowerNetwork net, int busId, double stepMw, double maxMw) =>
        Renewable.HostingCapacity.Analyze(net, busId, stepMw, maxMw);
}

public static class RenewableBuilderExtensions
{
    /// <summary>Registers <see cref="IRenewableService"/> (and the PowerFlow module it relies on).</summary>
    public static ElectricalBuilder AddRenewable(this ElectricalBuilder b)
    {
        b.AddPowerFlow();
        if (b.TryAddModule("Renewable")) b.Services.TryAddSingleton<IRenewableService, RenewableService>();
        return b;
    }
}
