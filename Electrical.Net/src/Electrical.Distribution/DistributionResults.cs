using System.Globalization;
using System.Numerics;
using System.Text;
using Electrical.Core.Units;

namespace Electrical.Distribution;

/// <summary>Solved voltages of one bus. Arrays are indexed by phase 0 = A, 1 = B, 2 = C; absent phases hold 0 / NaN.</summary>
public sealed class DistributionBusResult
{
    internal DistributionBusResult(string name, double baseKvLL, PhaseSet phases, Complex[] voltage)
    {
        Name = name;
        BaseKvLL = baseKvLL;
        Phases = phases;
        VoltageV = voltage;
        double vb = baseKvLL * 1000 / Math.Sqrt(3);
        VmPu = new double[3];
        VaDeg = new double[3];
        for (int p = 0; p < 3; p++)
        {
            VmPu[p] = phases.Has(p) ? voltage[p].Magnitude / vb : double.NaN;
            VaDeg[p] = phases.Has(p) ? voltage[p].Phase * 180 / Math.PI : double.NaN;
        }
        if (phases == PhaseSet.ABC)
        {
            VoltageUnbalanceFactor = new ThreePhase(voltage[0], voltage[1], voltage[2]).UnbalanceFactor();
            var ll = new[] { (voltage[0] - voltage[1]).Magnitude, (voltage[1] - voltage[2]).Magnitude, (voltage[2] - voltage[0]).Magnitude };
            double avg = ll.Average();
            NemaUnbalance = avg == 0 ? 0 : ll.Max(v => Math.Abs(v - avg)) / avg;
        }
        else
        {
            VoltageUnbalanceFactor = double.NaN;
            NemaUnbalance = double.NaN;
        }
    }

    public string Name { get; }
    /// <summary>Nominal line-to-line voltage [kV].</summary>
    public double BaseKvLL { get; }
    public PhaseSet Phases { get; }
    /// <summary>Phase-to-ground (neutral) voltage phasors [V].</summary>
    public Complex[] VoltageV { get; }
    /// <summary>Phase voltage magnitudes [pu of BaseKvLL/√3].</summary>
    public double[] VmPu { get; }
    /// <summary>Phase voltage angles [degrees].</summary>
    public double[] VaDeg { get; }
    /// <summary>Voltage unbalance factor |V2|/|V1| (IEC 61000-4-30), NaN unless all three phases exist.</summary>
    public double VoltageUnbalanceFactor { get; }
    /// <summary>NEMA MG-1 line voltage unbalance rate: max deviation of |V_LL| from the average / average.</summary>
    public double NemaUnbalance { get; }

    /// <summary>Line-to-line voltage phasor V_i − V_j [V].</summary>
    public Complex LineVoltage(int i, int j) => VoltageV[i] - VoltageV[j];

    public override string ToString() =>
        $"{Name}: " + string.Join("  ", Phases.Indices().Select(p => $"{"ABC"[p]} {VmPu[p]:0.0000}∠{VaDeg[p]:0.00}°"));
}

/// <summary>Currents and powers of one series element. Arrays are indexed by phase 0..2 (zero for absent phases).</summary>
public sealed class DistributionBranchFlow
{
    public required string Name { get; init; }
    public required DistributionBranchKind Kind { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required PhaseSet Phases { get; init; }
    /// <summary>Current entering the element at the from end [A].</summary>
    public required Complex[] CurrentFromA { get; init; }
    /// <summary>Current leaving the element at the to end (towards the load) [A].</summary>
    public required Complex[] CurrentToA { get; init; }
    /// <summary>Power entering at the from end per phase [kW + j·kvar].</summary>
    public required Complex[] PowerFromKva { get; init; }
    /// <summary>Power leaving at the to end per phase [kW + j·kvar].</summary>
    public required Complex[] PowerToKva { get; init; }

    /// <summary>Series losses [kW + j·kvar].</summary>
    public Complex LossesKva => PowerFromKva.Aggregate(Complex.Zero, (s, x) => s + x) - PowerToKva.Aggregate(Complex.Zero, (s, x) => s + x);
    public Complex TotalPowerFromKva => PowerFromKva.Aggregate(Complex.Zero, (s, x) => s + x);
    public double MaxCurrentA => Math.Max(CurrentFromA.Max(c => c.Magnitude), CurrentToA.Max(c => c.Magnitude));
    public override string ToString() => $"{Kind} {Name} {From}-{To}: P {TotalPowerFromKva.Real:0.0} kW, losses {LossesKva.Real:0.00} kW";
}

/// <summary>Result of an unbalanced (phase-frame) power flow.</summary>
public sealed class DistributionPowerFlowResult
{
    private readonly Dictionary<string, DistributionBusResult> _byBus;
    private readonly Dictionary<string, DistributionBranchFlow> _byBranch;

    internal DistributionPowerFlowResult(bool converged, int iterations, DistributionSolverMethod method, IReadOnlyList<DistributionBusResult> buses,
        IReadOnlyList<DistributionBranchFlow> branches, Complex[] sourceKva, Complex loadKva, IReadOnlyDictionary<string, int[]> taps,
        IReadOnlyList<string> warnings)
    {
        Converged = converged;
        Iterations = iterations;
        Method = method;
        Buses = buses;
        Branches = branches;
        SourcePowerKva = sourceKva;
        LoadKva = loadKva;
        RegulatorTaps = taps;
        Warnings = warnings;
        _byBus = buses.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        _byBranch = new(StringComparer.OrdinalIgnoreCase);
        foreach (var b in branches) _byBranch.TryAdd(b.Name, b);
    }

    public bool Converged { get; }
    /// <summary>Iterations of the last power flow solution.</summary>
    public int Iterations { get; }
    /// <summary>Number of control rounds (controller actions followed by a new solution).</summary>
    public int ControlIterations { get; internal set; }
    public DistributionSolverMethod Method { get; }
    public IReadOnlyList<DistributionBusResult> Buses { get; }
    public IReadOnlyList<DistributionBranchFlow> Branches { get; }
    /// <summary>Power delivered by the source per phase [kW + j·kvar].</summary>
    public Complex[] SourcePowerKva { get; }
    public Complex TotalSourceKva => SourcePowerKva[0] + SourcePowerKva[1] + SourcePowerKva[2];
    /// <summary>Power consumed by loads at the solved voltages [kW + j·kvar] (capacitors excluded).</summary>
    public Complex LoadKva { get; }
    /// <summary>Total series losses [kW + j·kvar].</summary>
    public Complex LossesKva => Branches.Aggregate(Complex.Zero, (s, b) => s + b.LossesKva);
    public double LossesKw => LossesKva.Real;
    /// <summary>Regulator taps per regulator name (index = phase).</summary>
    public IReadOnlyDictionary<string, int[]> RegulatorTaps { get; }
    public IReadOnlyList<string> Warnings { get; }

    public DistributionBusResult Bus(string name) =>
        _byBus.TryGetValue(name, out var b) ? b : throw new KeyNotFoundException($"Bus '{name}' not in result.");
    public DistributionBranchFlow Branch(string name) =>
        _byBranch.TryGetValue(name, out var b) ? b : throw new KeyNotFoundException($"Branch '{name}' not in result.");

    /// <summary>Lowest phase voltage (bus, phase, pu).</summary>
    public (string Bus, int Phase, double Vpu) MinVoltage => Extreme(true);
    /// <summary>Highest phase voltage (bus, phase, pu).</summary>
    public (string Bus, int Phase, double Vpu) MaxVoltage => Extreme(false);
    /// <summary>Largest IEC voltage unbalance factor over three-phase buses.</summary>
    public double MaxVoltageUnbalance => Buses.Where(b => !double.IsNaN(b.VoltageUnbalanceFactor)).Select(b => b.VoltageUnbalanceFactor).DefaultIfEmpty(0).Max();

    private (string, int, double) Extreme(bool min)
    {
        (string, int, double) best = ("", -1, min ? double.MaxValue : double.MinValue);
        foreach (var b in Buses)
            foreach (var p in b.Phases.Indices())
                if (min ? b.VmPu[p] < best.Item3 : b.VmPu[p] > best.Item3) best = (b.Name, p, b.VmPu[p]);
        return best;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine(ci, $"{Method}: converged={Converged} in {Iterations} it, control rounds {ControlIterations}; source {TotalSourceKva.Real:0.0} kW {TotalSourceKva.Imaginary:0.0} kvar, losses {LossesKw:0.00} kW");
        foreach (var b in Buses) sb.AppendLine(b.ToString());
        return sb.ToString();
    }
}
