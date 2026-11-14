using System.Numerics;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.Simulation;

/// <summary>
/// A discrete controller applied by <see cref="QstsSimulation"/> after each converged power flow. When any controller acts, the step is
/// re-solved (warm start) and the controllers are evaluated again, up to <see cref="QstsSimulation.MaxControlIterations"/> times.
/// Controllers keep run-time state (position, timers, counters) that <see cref="Initialize"/> resets at the start of every run;
/// one instance must not be shared by simulations running concurrently.
/// </summary>
public abstract class QstsController
{
    /// <summary>Display name used in <see cref="QstsResult.ControllerNames"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Short type tag ("OLTC", "Regulator", "Capacitor").</summary>
    public abstract string Kind { get; }

    /// <summary>Current discrete state: tap position (steps from neutral) or capacitor stages in service.</summary>
    public abstract double State { get; }

    /// <summary>Operations since <see cref="Initialize"/>: tap steps moved, or capacitor switching operations.</summary>
    public int Operations { get; protected set; }

    /// <summary>Resets the run-time state and writes the initial position into the simulation's working copy of the network.</summary>
    public abstract void Initialize(PowerNetwork network);

    /// <summary>
    /// Evaluates the controller against a converged solution of step <paramref name="step"/> and, if it acts, modifies
    /// <paramref name="network"/> (tap ratio, shunt) and returns true. May be called several times per step (after re-solves).
    /// </summary>
    public abstract bool Update(PowerNetwork network, PowerFlowResult result, int step);

    /// <summary>Label for result arrays.</summary>
    public virtual string DisplayName => Name ?? Kind;

    /// <summary>
    /// Delay timer helper: counts distinct steps spent outside the dead band. Returns true when the controller may act now.
    /// <c>delay = 0</c> acts immediately (also repeatedly within one step).
    /// </summary>
    private protected bool Armed(bool outOfBand, int step, int delay)
    {
        if (!outOfBand) { _timer = 0; _lastCounted = -1; return false; }
        if (step != _lastCounted) { _timer++; _lastCounted = step; }
        if (delay <= 0 || _timer > delay) { _timer = 0; return true; }
        return false;
    }

    private protected void ResetTimer() { _timer = 0; _lastCounted = -1; }

    private int _timer, _lastCounted = -1;
}

/// <summary>
/// On-load tap changer on a transformer branch, regulating the voltage magnitude of a controlled bus.
/// The branch ratio is <c>Tap = NominalRatio / (1 + n·TapStepPu)</c>, so a positive position <c>n</c> raises the to-side voltage by
/// about <c>n·TapStepPu</c> (MATPOWER convention: the tap is on the from side). Each step the controlled voltage is compared with
/// <see cref="TargetPu"/> ± <see cref="BandwidthPu"/>/2; after <see cref="DelaySteps"/> consecutive out-of-band steps the tap moves
/// by the number of positions needed to re-enter the band (at most <see cref="MaxTapsPerOperation"/>), clamped to
/// [<see cref="MinTap"/>, <see cref="MaxTap"/>].
/// </summary>
/// <param name="branchIndex">Index of the transformer in <see cref="PowerNetwork.Branches"/>.</param>
/// <param name="controlledBusId">Bus whose voltage is regulated (usually the transformer's to bus).</param>
public class OltcController(int branchIndex, int controlledBusId) : QstsController
{
    /// <summary>Index of the regulating branch in <see cref="PowerNetwork.Branches"/>.</summary>
    public int BranchIndex { get; } = branchIndex;
    /// <summary>Regulated bus id.</summary>
    public int ControlledBusId { get; } = controlledBusId;
    /// <summary>Voltage set-point [pu].</summary>
    public double TargetPu { get; init; } = 1.0;
    /// <summary>Total dead-band width [pu] (the controller acts when |V − target| &gt; bandwidth / 2).</summary>
    public double BandwidthPu { get; init; } = 0.0125;
    /// <summary>Voltage change per tap position [pu] (0.00625 = 5/8 %).</summary>
    public double TapStepPu { get; init; } = 0.00625;
    /// <summary>Lowest tap position (buck).</summary>
    public int MinTap { get; init; } = -16;
    /// <summary>Highest tap position (boost).</summary>
    public int MaxTap { get; init; } = 16;
    /// <summary>Position at the start of the run.</summary>
    public int InitialTap { get; init; }
    /// <summary>Consecutive out-of-band steps before a tap change (0 = act in the same step).</summary>
    public int DelaySteps { get; init; }
    /// <summary>Maximum tap positions moved per operation (default unlimited: move straight back into the band).</summary>
    public int MaxTapsPerOperation { get; init; } = int.MaxValue;
    /// <summary>Ratio at position 0; NaN = the branch's ratio in the base network (1 when it is 0).</summary>
    public double NominalRatio { get; init; } = double.NaN;

    private double _nominal;
    private int _tap;

    /// <inheritdoc />
    public override string Kind => "OLTC";
    /// <summary>Current tap position.</summary>
    public int TapPosition => _tap;
    /// <inheritdoc />
    public override double State => _tap;

    /// <inheritdoc />
    public override void Initialize(PowerNetwork network)
    {
        if (BranchIndex < 0 || BranchIndex >= network.Branches.Count) throw new ArgumentOutOfRangeException(nameof(BranchIndex), $"No branch {BranchIndex}.");
        if (!network.ContainsBus(ControlledBusId)) throw new ArgumentException($"Controlled bus {ControlledBusId} not found.");
        if (MinTap > MaxTap || TapStepPu <= 0) throw new ArgumentException("Invalid tap range or step.");
        var br = network.Branches[BranchIndex];
        _nominal = double.IsNaN(NominalRatio) ? br.EffectiveTap : NominalRatio;
        br.Kind = BranchKind.Transformer;
        _tap = Math.Clamp(InitialTap, MinTap, MaxTap);
        Operations = 0;
        ResetTimer();
        ApplyTap(network);
    }

    private void ApplyTap(PowerNetwork network) => network.Branches[BranchIndex].Tap = _nominal / (1 + _tap * TapStepPu);

    /// <summary>Measured (compensated) voltage magnitude [pu].</summary>
    protected virtual double Measure(PowerNetwork network, PowerFlowResult result) => result.Voltages[network.IndexOf(ControlledBusId)].Magnitude;

    /// <inheritdoc />
    public override bool Update(PowerNetwork network, PowerFlowResult result, int step)
    {
        double v = Measure(network, result);
        double dev = v - TargetPu, half = BandwidthPu / 2;
        bool low = dev < -half && _tap < MaxTap, high = dev > half && _tap > MinTap;
        if (!Armed(low || high, step, DelaySteps)) return false;
        int need = (int)Math.Ceiling((Math.Abs(dev) - half) / TapStepPu - 1e-9);
        need = Math.Clamp(need, 1, Math.Max(1, MaxTapsPerOperation));
        int next = Math.Clamp(_tap + (low ? need : -need), MinTap, MaxTap);
        if (next == _tap) return false;
        Operations += Math.Abs(next - _tap);
        _tap = next;
        ApplyTap(network);
        return true;
    }
}

/// <summary>
/// Step voltage regulator with line-drop compensation (LDC): an <see cref="OltcController"/> on a branch that regulates the estimated
/// voltage at a remote load centre, <c>|V_to − (R + jX)·I_to|</c>, where <c>I_to</c> is the branch current leaving the to bus towards
/// the feeder and R/X are the compensator settings in pu on the system base. Defaults: ±16 steps of 0.625 %, 2 V band on 120 V
/// (0.0167 pu), target 1.0 pu.
/// </summary>
/// <param name="branchIndex">Regulator branch index (its to bus is the regulated terminal).</param>
/// <param name="regulatedBusId">The branch's to bus.</param>
public sealed class VoltageRegulatorController(int branchIndex, int regulatedBusId) : OltcController(branchIndex, regulatedBusId)
{
    /// <summary>Compensator resistance [pu on the system base].</summary>
    public double LdcR { get; init; }
    /// <summary>Compensator reactance [pu on the system base].</summary>
    public double LdcX { get; init; }

    /// <inheritdoc />
    public override string Kind => "Regulator";

    /// <summary>Creates a regulator on <paramref name="branchIndex"/> with the 120 V-class defaults (band 2 V / 120 V).</summary>
    public static VoltageRegulatorController Create(PowerNetwork network, int branchIndex, double ldcR = 0, double ldcX = 0, double targetPu = 1.0) =>
        new(branchIndex, network.Branches[branchIndex].To) { LdcR = ldcR, LdcX = ldcX, TargetPu = targetPu, BandwidthPu = 2.0 / 120 };

    /// <inheritdoc />
    protected override double Measure(PowerNetwork network, PowerFlowResult result)
    {
        var vTo = result.Voltages[network.IndexOf(ControlledBusId)];
        if (LdcR == 0 && LdcX == 0) return vTo.Magnitude;
        var br = result.Branches[BranchIndex];
        // power leaving the to bus into the feeder = −(PTo + jQTo) [MW, Mvar] → current in pu
        var s = new Complex(-br.PTo, -br.QTo) / network.BaseMva;
        var i = vTo.Magnitude > 1e-9 ? Complex.Conjugate(s / vTo) : Complex.Zero;
        return (vTo - new Complex(LdcR, LdcX) * i).Magnitude;
    }
}

/// <summary>Control quantity of a <see cref="CapacitorController"/>.</summary>
public enum CapacitorControlMode
{
    /// <summary>Switch on when the monitored voltage is below <see cref="CapacitorController.OnThreshold"/> [pu], off above <see cref="CapacitorController.OffThreshold"/>.</summary>
    Voltage,
    /// <summary>
    /// Switch on when the reactive power flowing from-to on the monitored branch exceeds <see cref="CapacitorController.OnThreshold"/> [Mvar],
    /// off when it falls below <see cref="CapacitorController.OffThreshold"/> [Mvar] (negative = reverse flow).
    /// </summary>
    ReactivePower,
}

/// <summary>
/// Switched capacitor bank with <see cref="Stages"/> stages of <see cref="StageMvar"/> each (Mvar at 1 pu) at bus <see cref="BusId"/>.
/// The bank adds <c>stages·StageMvar</c> to the bus shunt <see cref="Bus.Bs"/> present at the start of the run (so it overrides
/// <see cref="SimulationEventKind.ShuntSet"/> events on the same bus). One stage is switched per operation after
/// <see cref="DelaySteps"/> consecutive steps beyond a threshold.
/// </summary>
/// <param name="busId">Capacitor bus id.</param>
/// <param name="stageMvar">Size of one stage [Mvar at 1 pu].</param>
/// <param name="stages">Number of stages.</param>
public sealed class CapacitorController(int busId, double stageMvar, int stages = 1) : QstsController
{
    /// <summary>Capacitor bus id.</summary>
    public int BusId { get; } = busId;
    /// <summary>Stage size [Mvar at 1 pu].</summary>
    public double StageMvar { get; } = stageMvar;
    /// <summary>Number of stages.</summary>
    public int Stages { get; } = stages;
    /// <summary>Control quantity.</summary>
    public CapacitorControlMode Mode { get; init; } = CapacitorControlMode.Voltage;
    /// <summary>Switch-on threshold: voltage [pu] (on below) or reactive flow [Mvar] (on above).</summary>
    public double OnThreshold { get; init; } = 0.97;
    /// <summary>Switch-off threshold: voltage [pu] (off above) or reactive flow [Mvar] (off below).</summary>
    public double OffThreshold { get; init; } = 1.03;
    /// <summary>Bus monitored in voltage mode (default: the capacitor bus).</summary>
    public int? MonitoredBusId { get; init; }
    /// <summary>Branch monitored in reactive-power mode (from-end Q).</summary>
    public int MonitoredBranchIndex { get; init; } = -1;
    /// <summary>Consecutive steps beyond a threshold before switching (0 = immediately).</summary>
    public int DelaySteps { get; init; }
    /// <summary>Stages in service at the start of the run.</summary>
    public int InitialStages { get; init; }

    private double _baseBs;
    private int _on;

    /// <inheritdoc />
    public override string Kind => "Capacitor";
    /// <summary>Stages currently in service.</summary>
    public int StagesOn => _on;
    /// <inheritdoc />
    public override double State => _on;

    /// <inheritdoc />
    public override void Initialize(PowerNetwork network)
    {
        if (!network.ContainsBus(BusId)) throw new ArgumentException($"Capacitor bus {BusId} not found.");
        if (Stages < 1) throw new ArgumentException("At least one stage.");
        if (Mode == CapacitorControlMode.ReactivePower && (MonitoredBranchIndex < 0 || MonitoredBranchIndex >= network.Branches.Count))
            throw new ArgumentException("Reactive-power mode needs a valid MonitoredBranchIndex.");
        if (Mode == CapacitorControlMode.Voltage && OnThreshold >= OffThreshold) throw new ArgumentException("Voltage mode needs OnThreshold < OffThreshold.");
        if (Mode == CapacitorControlMode.ReactivePower && OnThreshold <= OffThreshold) throw new ArgumentException("Var mode needs OnThreshold > OffThreshold.");
        _baseBs = network.GetBus(BusId).Bs;
        _on = Math.Clamp(InitialStages, 0, Stages);
        Operations = 0;
        ResetTimer();
        Apply(network);
    }

    private void Apply(PowerNetwork network) => network.GetBus(BusId).Bs = _baseBs + _on * StageMvar;

    /// <inheritdoc />
    public override bool Update(PowerNetwork network, PowerFlowResult result, int step)
    {
        bool wantOn, wantOff;
        if (Mode == CapacitorControlMode.Voltage)
        {
            double v = result.Voltages[network.IndexOf(MonitoredBusId ?? BusId)].Magnitude;
            wantOn = v < OnThreshold; wantOff = v > OffThreshold;
        }
        else
        {
            double q = result.Branches[MonitoredBranchIndex].QFrom;
            wantOn = q > OnThreshold; wantOff = q < OffThreshold;
        }
        wantOn &= _on < Stages;
        wantOff &= _on > 0;
        if (!Armed(wantOn || wantOff, step, DelaySteps)) return false;
        _on += wantOn ? 1 : -1;
        Operations++;
        Apply(network);
        return true;
    }
}
