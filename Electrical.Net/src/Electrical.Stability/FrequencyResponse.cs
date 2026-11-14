using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Stability;

/// <summary>
/// Aggregate (single-area) frequency-response model on the system base:
/// 2H·dΔf/dt = ΔP_m − ΔP_L − D·Δf;  T_g·dΔP_v/dt = −Δf/R − ΔP_v;  T_t·dΔP_m/dt = ΔP_v − ΔP_m  (Δf, ΔP in pu).
/// </summary>
/// <param name="H">Aggregate inertia constant [s] on the system base.</param>
/// <param name="D">Load damping [pu power / pu frequency] (typically 1–2).</param>
/// <param name="R">Governor droop [pu] (e.g. 0.05 = 5 %); ∞ disables primary control.</param>
/// <param name="DisturbancePu">Load increase / generation loss ΔP_L at t = 0 [pu].</param>
/// <param name="GovernorTimeS">Governor time constant T_g [s].</param>
/// <param name="TurbineTimeS">Turbine time constant T_t [s].</param>
/// <param name="FrequencyHz">Nominal frequency [Hz].</param>
public sealed record FrequencyResponseParameters(double H, double D, double R, double DisturbancePu, double GovernorTimeS = 0.2, double TurbineTimeS = 0.5,
    double FrequencyHz = 50)
{
    /// <summary>Aggregates the in-service synchronous generators of a network: H_sys = Σ H_i·S_i / S_base; disturbance in MW.</summary>
    public static FrequencyResponseParameters FromNetwork(PowerNetwork network, double disturbanceMw, double droop = 0.05, double loadDamping = 1.0,
        double governorTimeS = 0.2, double turbineTimeS = 0.5)
    {
        double h = network.Generators.Where(g => g.InService && g.H > 0 && g.H < 1e5).Sum(g => g.H * g.MBase) / network.BaseMva;
        return new FrequencyResponseParameters(h, loadDamping, droop, disturbanceMw / network.BaseMva, governorTimeS, turbineTimeS,
            network.FrequencyHz > 0 ? network.FrequencyHz : 50);
    }

    /// <summary>Initial rate of change of frequency ROCOF = −ΔP_L·f₀/(2H) [Hz/s].</summary>
    public double InitialRocofHzPerS => -DisturbancePu * FrequencyHz / (2 * H);
    /// <summary>Quasi-steady-state deviation Δf_ss = −ΔP_L/(D + 1/R)·f₀ [Hz].</summary>
    public double SteadyStateDeviationHz => -DisturbancePu / (D + (double.IsInfinity(R) ? 0 : 1 / R)) * FrequencyHz;
}

/// <summary>Frequency-response trajectory and metrics.</summary>
public sealed record FrequencyResponseResult(
    double[] Time, double[] FrequencyHz, double[] DeltaFrequencyHz, double[] MechanicalPowerPu, double InitialRocofHzPerS, double NadirHz,
    double NadirTimeS, double SteadyStateDeviationHz);

/// <summary>Aggregate swing-equation frequency response with governor droop and load damping (RK4).</summary>
public static class FrequencyResponse
{
    public static FrequencyResponseResult Simulate(FrequencyResponseParameters p, double endTimeS = 30, double stepS = 0.005)
    {
        double f = 0, pv = 0, pmech = 0;
        int n = (int)Math.Round(endTimeS / stepS);
        var t = new double[n + 1]; var fr = new double[n + 1]; var df = new double[n + 1]; var pm = new double[n + 1];
        double invR = double.IsInfinity(p.R) ? 0 : 1 / p.R;
        (double, double, double) F(double x, double v, double m) => (
            (m - p.DisturbancePu - p.D * x) / (2 * p.H),
            (-x * invR - v) / p.GovernorTimeS,
            (v - m) / p.TurbineTimeS);
        fr[0] = p.FrequencyHz;
        double nadir = 0, nadirT = 0;
        for (int k = 1; k <= n; k++)
        {
            double h = stepS;
            var (a1, b1, c1) = F(f, pv, pmech);
            var (a2, b2, c2) = F(f + h / 2 * a1, pv + h / 2 * b1, pmech + h / 2 * c1);
            var (a3, b3, c3) = F(f + h / 2 * a2, pv + h / 2 * b2, pmech + h / 2 * c2);
            var (a4, b4, c4) = F(f + h * a3, pv + h * b3, pmech + h * c3);
            f += h / 6 * (a1 + 2 * a2 + 2 * a3 + a4);
            pv += h / 6 * (b1 + 2 * b2 + 2 * b3 + b4);
            pmech += h / 6 * (c1 + 2 * c2 + 2 * c3 + c4);
            t[k] = k * h; df[k] = f * p.FrequencyHz; fr[k] = p.FrequencyHz + df[k]; pm[k] = pmech;
            if (Math.Abs(df[k]) > Math.Abs(nadir)) { nadir = df[k]; nadirT = t[k]; }
        }
        return new FrequencyResponseResult(t, fr, df, pm, p.InitialRocofHzPerS, p.FrequencyHz + nadir, nadirT, p.SteadyStateDeviationHz);
    }
}

/// <summary>Stability service resolvable from DI.</summary>
public interface IStabilityService
{
    TransientStabilityResult Simulate(PowerNetwork network, IEnumerable<StabilityEvent> events, TransientStabilityOptions? options = null);
    double CriticalClearingTime(PowerNetwork network, int faultBusId, int? tripBranchIndex = null);
    SmallSignalResult SmallSignal(PowerNetwork network);
    PvCurveResult PvCurve(PowerNetwork network, PvCurveOptions? options = null);
    LIndexResult LIndex(PowerNetwork network);
}

internal sealed class StabilityService : IStabilityService
{
    public TransientStabilityResult Simulate(PowerNetwork network, IEnumerable<StabilityEvent> events, TransientStabilityOptions? options = null) =>
        TransientStability.Simulate(network, events, options);
    public double CriticalClearingTime(PowerNetwork network, int faultBusId, int? tripBranchIndex = null) =>
        TransientStability.CriticalClearingTime(ClassicalModel.FromPowerFlow(network), faultBusId, tripBranchIndex);
    public SmallSignalResult SmallSignal(PowerNetwork network) => Stability.SmallSignal.Analyze(network);
    public PvCurveResult PvCurve(PowerNetwork network, PvCurveOptions? options = null) => VoltageStability.PvCurve(network, options);
    public LIndexResult LIndex(PowerNetwork network) => VoltageStability.LIndex(network);
}

public static class StabilityBuilderExtensions
{
    /// <summary>Registers <see cref="IStabilityService"/> (and the power-flow module).</summary>
    public static ElectricalBuilder AddStability(this ElectricalBuilder b)
    {
        b.AddPowerFlow();
        if (b.TryAddModule("Stability")) b.Services.TryAddSingleton<IStabilityService, StabilityService>();
        return b;
    }
}
