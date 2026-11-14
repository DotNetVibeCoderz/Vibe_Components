using System.Numerics;
using Electrical.Core.Network;

namespace Electrical.PowerFlow;

public enum PowerFlowMethod { NewtonRaphson, GaussSeidel, FastDecoupled, DC, BackwardForwardSweep }

public sealed record PowerFlowOptions
{
    public PowerFlowMethod Method { get; init; } = PowerFlowMethod.NewtonRaphson;
    /// <summary>Convergence tolerance on the power mismatch [pu].</summary>
    public double Tolerance { get; init; } = 1e-8;
    public int MaxIterations { get; init; } = 30;
    /// <summary>Convert PV buses to PQ when generator reactive limits are violated.</summary>
    public bool EnforceQLimits { get; init; }
    /// <summary>Start from |V| = 1 (PV: set-point), θ = 0 instead of the bus values stored in the network.</summary>
    public bool FlatStart { get; init; } = true;

    public static PowerFlowOptions Default { get; } = new();
}

public sealed record BusResult(int BusId, string? Name, double Vm, double VaDeg, double PGen, double QGen, double PLoad, double QLoad, BusType Type)
{
    /// <summary>Voltage in kV (line-to-line).</summary>
    public double Kv(double baseKv) => Vm * baseKv;
}

public sealed record BranchResult(int Index, int From, int To, string? Name, double PFrom, double QFrom, double PTo, double QTo, double RateA)
{
    public double PLoss => PFrom + PTo;
    public double QLoss => QFrom + QTo;
    public double SFrom => Math.Sqrt(PFrom * PFrom + QFrom * QFrom);
    public double STo => Math.Sqrt(PTo * PTo + QTo * QTo);
    /// <summary>Loading in % of RateA (0 when unrated).</summary>
    public double LoadingPercent => RateA > 0 ? 100 * Math.Max(SFrom, STo) / RateA : 0;
}

public sealed record GeneratorResult(int Index, int Bus, string? Name, double P, double Q, bool AtQLimit);

/// <summary>Solved operating point. Powers in MW / Mvar, voltages in pu / degrees.</summary>
public sealed class PowerFlowResult
{
    public required PowerNetwork Network { get; init; }
    public required PowerFlowMethod Method { get; init; }
    public required bool Converged { get; init; }
    public required int Iterations { get; init; }
    public required double MaxMismatch { get; init; }
    public required Complex[] Voltages { get; init; }
    public required IReadOnlyList<BusResult> Buses { get; init; }
    public required IReadOnlyList<BranchResult> Branches { get; init; }
    public required IReadOnlyList<GeneratorResult> Generators { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>Mismatch norm per iteration (convergence history).</summary>
    public IReadOnlyList<double> History { get; init; } = [];

    public double TotalGenerationMw => Generators.Sum(g => g.P);
    public double TotalGenerationMvar => Generators.Sum(g => g.Q);
    public double TotalLoadMw => Buses.Sum(b => b.PLoad);
    public double TotalLoadMvar => Buses.Sum(b => b.QLoad);
    public double LossesMw => Branches.Sum(b => b.PLoss);
    public double LossesMvar => Branches.Sum(b => b.QLoss);

    public BusResult Bus(int busId) => Buses.First(b => b.BusId == busId);
    public BusResult MinVoltageBus => Buses.MinBy(b => b.Vm)!;
    public BusResult MaxVoltageBus => Buses.MaxBy(b => b.Vm)!;
    public BranchResult? MostLoadedBranch => Branches.Where(b => b.RateA > 0).MaxBy(b => b.LoadingPercent);

    /// <summary>Buses outside their [Vmin, Vmax] band.</summary>
    public IEnumerable<BusResult> VoltageViolations() =>
        Buses.Where(b => { var bus = Network.GetBus(b.BusId); return b.Vm < bus.Vmin - 1e-6 || b.Vm > bus.Vmax + 1e-6; });

    public IEnumerable<BranchResult> Overloads(double thresholdPercent = 100) => Branches.Where(b => b.LoadingPercent > thresholdPercent);

    /// <summary>Writes the solved voltages and generator outputs back into the network (warm start for later studies).</summary>
    public void ApplyTo(PowerNetwork net)
    {
        for (int i = 0; i < Buses.Count; i++)
        {
            var bus = net.GetBus(Buses[i].BusId);
            bus.Vm = Buses[i].Vm;
            bus.VaDeg = Buses[i].VaDeg;
        }
        foreach (var g in Generators)
        {
            net.Generators[g.Index].P = g.P;
            net.Generators[g.Index].Q = g.Q;
        }
    }

    public override string ToString() =>
        $"{Method}: {(Converged ? "converged" : "NOT converged")} in {Iterations} it, mismatch {MaxMismatch:E2}, " +
        $"gen {TotalGenerationMw:0.###} MW, load {TotalLoadMw:0.###} MW, losses {LossesMw:0.###} MW, Vmin {MinVoltageBus.Vm:0.0000} pu @ bus {MinVoltageBus.BusId}";
}
