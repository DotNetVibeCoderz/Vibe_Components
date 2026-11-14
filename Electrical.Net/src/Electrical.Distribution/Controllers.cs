using System.Numerics;

namespace Electrical.Distribution;

/// <summary>Timing context for controllers: simulation time [s], time-step length [s] and whether delays are ignored (static solution).</summary>
public readonly record struct ControlTiming(double TimeSeconds, double StepSeconds, bool IgnoreDelays);

/// <summary>A controller that observes a power flow result and changes the network (taps, switch states).</summary>
public interface IDistributionController
{
    string Name { get; }
    /// <summary>Cumulative operations (tap steps or switching operations) since construction or <see cref="Reset"/>.</summary>
    int Operations { get; }
    /// <summary>Observes <paramref name="result"/>; returns true if it changed the network (a new solution is needed).</summary>
    bool Control(DistributionNetwork network, DistributionPowerFlowResult result, ControlTiming timing);
    /// <summary>Clears timers and the operation counter.</summary>
    void Reset();
}

/// <summary>Delay timer: a condition must persist for <c>delay</c> seconds; the observed state is assumed to hold for the time step.</summary>
internal sealed class DelayTimer
{
    private double? _since;
    public bool Elapsed(bool condition, double delay, ControlTiming t)
    {
        if (!condition) { _since = null; return false; }
        if (t.IgnoreDelays) return true;
        _since ??= t.TimeSeconds;
        return t.TimeSeconds - _since.Value + t.StepSeconds >= delay - 1e-9;
    }
    public void Clear() => _since = null;
}

/// <summary>
/// Step-voltage regulator control with line-drop compensator (Kersting §7.2): relay voltage on the 120 V base
/// V_relay = |V_out/N_PT − (R' + jX')·I_line/CT_p|; taps move when V_relay leaves VoltageLevel ± Bandwidth/2, by enough steps
/// (0.75 V each on 120 V for 0.625 %) to re-enter the band. Each phase is controlled independently unless <see cref="Ganged"/>.
/// </summary>
public sealed class VoltageRegulatorController(VoltageRegulator regulator, string? name = null) : IDistributionController
{
    private readonly DelayTimer[] _timers = [new(), new(), new()];

    public string Name { get; } = name ?? regulator.Name;
    public VoltageRegulator Regulator { get; } = regulator;
    /// <summary>Voltage set-point [V on the 120 V base].</summary>
    public double VoltageLevel { get; set; } = 120;
    /// <summary>Total bandwidth [V on the 120 V base].</summary>
    public double Bandwidth { get; set; } = 2;
    /// <summary>Potential transformer ratio N_PT.</summary>
    public double PtRatio { get; set; } = 20;
    /// <summary>Current transformer primary rating CT_p [A].</summary>
    public double CtPrimary { get; set; } = 600;
    /// <summary>Line-drop compensator R' [V].</summary>
    public double R { get; set; }
    /// <summary>Line-drop compensator X' [V].</summary>
    public double X { get; set; }
    /// <summary>Initial time delay [s].</summary>
    public double TimeDelay { get; set; } = 30;
    /// <summary>All phases follow the phase with the largest deviation (single tap changer).</summary>
    public bool Ganged { get; set; }
    /// <summary>Phases to control (default: all phases of the regulator).</summary>
    public PhaseSet ControlledPhases { get; set; } = regulator.Phases;
    public int Operations { get; private set; }

    /// <summary>Relay voltage of a phase on the 120 V base.</summary>
    public double RelayVoltage(DistributionPowerFlowResult result, int phase)
    {
        var vOut = result.Bus(Regulator.To).VoltageV[phase];
        var iLine = result.Branch(Regulator.Name).CurrentToA[phase];
        return (vOut / PtRatio - new Complex(R, X) * iLine / CtPrimary).Magnitude;
    }

    public bool Control(DistributionNetwork network, DistributionPowerFlowResult result, ControlTiming timing)
    {
        double step = Regulator.StepPercent / 100 * 120;
        double low = VoltageLevel - Bandwidth / 2, high = VoltageLevel + Bandwidth / 2;
        var phases = (ControlledPhases & Regulator.Phases).Indices();
        var moves = new Dictionary<int, int>();
        foreach (var p in phases)
        {
            double vr = RelayVoltage(result, p);
            int steps = vr < low ? (int)Math.Ceiling((low - vr) / step - 1e-9) : vr > high ? -(int)Math.Ceiling((vr - high) / step - 1e-9) : 0;
            int target = Math.Clamp(Regulator.Taps[p] + steps, -Regulator.MaxTap, Regulator.MaxTap);
            bool want = target != Regulator.Taps[p];
            if (_timers[p].Elapsed(want, TimeDelay, timing)) moves[p] = target - Regulator.Taps[p];
        }
        if (moves.Count == 0) return false;
        if (Ganged)
        {
            int d = moves.Values.OrderByDescending(Math.Abs).First();
            foreach (var p in phases) moves[p] = d;
        }
        bool changed = false;
        foreach (var (p, d) in moves)
        {
            int before = Regulator.Taps[p];
            Regulator.SetTap(p, before + d);
            int delta = Math.Abs(Regulator.Taps[p] - before);
            if (delta > 0) { Operations += delta; changed = true; _timers[p].Clear(); }
        }
        return changed;
    }

    public void Reset()
    {
        Operations = 0;
        foreach (var t in _timers) t.Clear();
    }
}

/// <summary>
/// On-load tap changer control of a <see cref="DistributionTransformer"/> (secondary tap): keeps the monitored bus voltage within
/// TargetPu ± BandwidthPu/2 with a time delay, in steps of <see cref="StepPu"/> between <see cref="MinTapPu"/> and <see cref="MaxTapPu"/>.
/// </summary>
public sealed class OltcController(DistributionTransformer transformer, string monitoredBus, string? name = null) : IDistributionController
{
    private readonly DelayTimer _timer = new();

    public string Name { get; } = name ?? transformer.Name + ".oltc";
    public DistributionTransformer Transformer { get; } = transformer;
    public string MonitoredBus { get; set; } = monitoredBus;
    /// <summary>Monitored phase index, or −1 for the average of the available phases.</summary>
    public int MonitoredPhase { get; set; } = -1;
    /// <summary>Target voltage [pu].</summary>
    public double TargetPu { get; set; } = 1.0;
    /// <summary>Total deadband [pu].</summary>
    public double BandwidthPu { get; set; } = 0.0167;
    /// <summary>Tap step [pu].</summary>
    public double StepPu { get; set; } = 0.00625;
    public double MinTapPu { get; set; } = 0.9;
    public double MaxTapPu { get; set; } = 1.1;
    /// <summary>Time delay [s].</summary>
    public double TimeDelay { get; set; } = 60;
    public int Operations { get; private set; }
    /// <summary>Current tap position in steps from 1.0 pu.</summary>
    public int TapPosition => (int)Math.Round((Transformer.TapSecondary - 1) / StepPu);

    public double MeasuredPu(DistributionPowerFlowResult result)
    {
        var b = result.Bus(MonitoredBus);
        return MonitoredPhase >= 0 ? b.VmPu[MonitoredPhase] : b.Phases.Indices().Average(p => b.VmPu[p]);
    }

    public bool Control(DistributionNetwork network, DistributionPowerFlowResult result, ControlTiming timing)
    {
        double v = MeasuredPu(result);
        double low = TargetPu - BandwidthPu / 2, high = TargetPu + BandwidthPu / 2;
        int steps = v < low ? (int)Math.Ceiling((low - v) / StepPu - 1e-9) : v > high ? -(int)Math.Ceiling((v - high) / StepPu - 1e-9) : 0;
        double target = Math.Clamp(Transformer.TapSecondary + steps * StepPu, MinTapPu, MaxTapPu);
        bool want = Math.Abs(target - Transformer.TapSecondary) > 1e-9;
        if (!_timer.Elapsed(want, TimeDelay, timing)) return false;
        int moved = (int)Math.Round(Math.Abs(target - Transformer.TapSecondary) / StepPu);
        Transformer.TapSecondary = target;
        Operations += Math.Max(moved, 1);
        _timer.Clear();
        return true;
    }

    public void Reset() { Operations = 0; _timer.Clear(); }
}

/// <summary>Capacitor control strategies.</summary>
public enum CapacitorControlMode
{
    /// <summary>On when the monitored voltage [pu] falls below OnSetting, off above OffSetting.</summary>
    Voltage,
    /// <summary>On when the reactive power [kvar] flowing in the monitored element exceeds OnSetting, off below OffSetting.</summary>
    ReactivePower,
    /// <summary>On between OnSetting and OffSetting [hour of day].</summary>
    Time,
    /// <summary>On when the monitored flow is lagging with power factor below OnSetting; off when leading with power factor below OffSetting.</summary>
    PowerFactor,
}

/// <summary>Switched capacitor controller (voltage, var, time or power-factor control) with on/off delays.</summary>
public sealed class CapacitorController(ShuntCapacitor capacitor, CapacitorControlMode mode, string? name = null) : IDistributionController
{
    private readonly DelayTimer _onTimer = new(), _offTimer = new();

    public string Name { get; } = name ?? capacitor.Name + ".ctrl";
    public ShuntCapacitor Capacitor { get; } = capacitor;
    public CapacitorControlMode Mode { get; set; } = mode;
    /// <summary>Monitored bus for voltage control (default: the capacitor bus).</summary>
    public string? MonitoredBus { get; set; }
    /// <summary>Monitored phase (−1 = average) for voltage control.</summary>
    public int MonitoredPhase { get; set; } = -1;
    /// <summary>Monitored series element (by name) for var / power-factor control; its from-end flow is used.</summary>
    public string? MonitoredElement { get; set; }
    public double OnSetting { get; set; }
    public double OffSetting { get; set; }
    /// <summary>Delay before switching on [s].</summary>
    public double OnDelay { get; set; } = 15;
    /// <summary>Delay before switching off [s].</summary>
    public double OffDelay { get; set; } = 15;
    public int Operations { get; private set; }

    public bool Control(DistributionNetwork network, DistributionPowerFlowResult result, ControlTiming timing)
    {
        bool wantOn = false, wantOff = false;
        switch (Mode)
        {
            case CapacitorControlMode.Voltage:
            {
                var b = result.Bus(MonitoredBus ?? Capacitor.Bus);
                double v = MonitoredPhase >= 0 ? b.VmPu[MonitoredPhase] : b.Phases.Indices().Average(p => b.VmPu[p]);
                wantOn = v < OnSetting; wantOff = v > OffSetting;
                break;
            }
            case CapacitorControlMode.ReactivePower:
            {
                double q = Flow(result).Imaginary;
                wantOn = q > OnSetting; wantOff = q < OffSetting;
                break;
            }
            case CapacitorControlMode.PowerFactor:
            {
                var s = Flow(result);
                double pf = s.Magnitude < 1e-9 ? 1 : Math.Abs(s.Real) / s.Magnitude;
                wantOn = s.Imaginary > 0 && pf < OnSetting;
                wantOff = s.Imaginary < 0 && pf < OffSetting;
                break;
            }
            case CapacitorControlMode.Time:
            {
                double hour = timing.TimeSeconds / 3600 % 24;
                bool inWindow = OnSetting <= OffSetting ? hour >= OnSetting && hour < OffSetting : hour >= OnSetting || hour < OffSetting;
                wantOn = inWindow; wantOff = !inWindow;
                break;
            }
        }
        if (!Capacitor.IsOn && _onTimer.Elapsed(wantOn, OnDelay, timing))
        {
            Capacitor.IsOn = true; Operations++; _onTimer.Clear(); _offTimer.Clear();
            return true;
        }
        if (Capacitor.IsOn && _offTimer.Elapsed(wantOff, OffDelay, timing))
        {
            Capacitor.IsOn = false; Operations++; _onTimer.Clear(); _offTimer.Clear();
            return true;
        }
        if (Capacitor.IsOn) _onTimer.Clear(); else _offTimer.Clear();
        return false;
    }

    private Complex Flow(DistributionPowerFlowResult r)
    {
        if (MonitoredElement is null) throw new InvalidOperationException($"{Name}: MonitoredElement is required for {Mode} control.");
        return r.Branch(MonitoredElement).TotalPowerFromKva;
    }

    public void Reset() { Operations = 0; _onTimer.Clear(); _offTimer.Clear(); }
}
