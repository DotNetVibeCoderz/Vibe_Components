using Electrical.Core.Network;
using Electrical.Optimization;

namespace Electrical.Markets;

/// <summary>Price-quantity block of a supply offer or demand bid.</summary>
/// <param name="QuantityMw">Block size [MW].</param>
/// <param name="Price">Offer price (supply) or willingness to pay (demand) [$/MWh].</param>
public readonly record struct OfferBlock(double QuantityMw, double Price);

/// <summary>
/// Thermal unit for <see cref="SecurityConstrainedUnitCommitment"/>. The generation cost is either a quadratic <see cref="Cost"/>
/// C(P) = C2·P² + C1·P + C0 [$/h] (approximated by <see cref="ScucOptions.CostSegments"/> chords between Pmin and Pmax, C(Pmin) charged per
/// committed hour) or explicit price-quantity <see cref="Blocks"/> starting at 0 MW plus a <see cref="NoLoadCost"/>.
/// </summary>
public sealed record ScucUnit
{
    public required string Name { get; init; }
    /// <summary>Bus id of the connection point (ignored without a network).</summary>
    public int Bus { get; init; }
    /// <summary>Minimum stable output while committed [MW].</summary>
    public double PminMw { get; init; }
    /// <summary>Maximum output [MW] (with <see cref="Blocks"/> it defaults to the block total when left at 0).</summary>
    public double PmaxMw { get; init; }
    /// <summary>Quadratic cost [$/h]; ignored when <see cref="Blocks"/> is set.</summary>
    public PolynomialCost Cost { get; init; }
    /// <summary>Piecewise-linear cost as price-quantity blocks from 0 MW [MW, $/MWh] (sorted by price when used).</summary>
    public IReadOnlyList<OfferBlock>? Blocks { get; init; }
    /// <summary>Cost per committed hour [$/h] (only with <see cref="Blocks"/>; with <see cref="Cost"/> the constant C0 plays this role).</summary>
    public double NoLoadCost { get; init; }
    /// <summary>Cost of one start [$].</summary>
    public double StartupCost { get; init; }
    /// <summary>Minimum up time [periods].</summary>
    public int MinUpHours { get; init; } = 1;
    /// <summary>Minimum down time [periods].</summary>
    public int MinDownHours { get; init; } = 1;
    /// <summary>Ramp-up limit [MW/h] (∞ = none).</summary>
    public double RampUpMwPerHour { get; init; } = double.PositiveInfinity;
    /// <summary>Ramp-down limit [MW/h] (∞ = none).</summary>
    public double RampDownMwPerHour { get; init; } = double.PositiveInfinity;
    /// <summary>Largest output in the first period after a start [MW] (NaN = max(Pmin, ramp-up of one period)).</summary>
    public double StartupRampMw { get; init; } = double.NaN;
    /// <summary>Largest output in the last period before a shutdown [MW] (NaN = max(Pmin, ramp-down of one period)).</summary>
    public double ShutdownRampMw { get; init; } = double.NaN;
    /// <summary>Initial status: +h = on for h periods, −h = off for h periods.</summary>
    public int InitialHours { get; init; } = -24;
    /// <summary>Output in the period before the horizon [MW] (NaN = no ramp constraint on the first period).</summary>
    public double InitialOutputMw { get; init; } = double.NaN;
    /// <summary>Fixed commitment per period (null = optimised). Used for "given commitment" clearing.</summary>
    public IReadOnlyList<bool>? FixedCommitment { get; init; }

    internal double EffectivePmax => Blocks is { Count: > 0 } && PmaxMw <= 0 ? Blocks.Sum(b => b.QuantityMw) : PmaxMw;

    /// <summary>Unit commitment data of <see cref="UnitCommitment"/> (Wood &amp; Wollenberg style).</summary>
    public static ScucUnit FromUcUnit(UcUnit u, int bus = 0) => new()
    {
        Name = u.Name, Bus = bus, PminMw = u.PminMw, PmaxMw = u.PmaxMw, Cost = u.Cost, StartupCost = u.StartupCost, MinUpHours = u.MinUpHours,
        MinDownHours = u.MinDownHours, InitialHours = u.InitialHours,
    };

    /// <summary>Unit from a network generator (cost, limits and <see cref="Generator.RampRate"/>), initially on.</summary>
    public static ScucUnit FromGenerator(Generator g, int index = 0, double startupCost = 0, int minUpHours = 1, int minDownHours = 1,
        int initialHours = 24) => new()
    {
        Name = g.Name ?? $"G{index + 1}@{g.Bus}", Bus = g.Bus, PminMw = g.Pmin, PmaxMw = g.Pmax, Cost = g.Cost, StartupCost = startupCost,
        MinUpHours = minUpHours, MinDownHours = minDownHours, RampUpMwPerHour = g.RampRate, RampDownMwPerHour = g.RampRate,
        InitialHours = initialHours,
    };
}

/// <summary>Options for <see cref="SecurityConstrainedUnitCommitment"/> and <see cref="MarketClearing"/>.</summary>
public sealed record ScucOptions
{
    /// <summary>Spinning reserve requirement as a fraction of the hourly demand: Σ(Pmax·u − P) ≥ f·D + <see cref="ReserveMw"/>.</summary>
    public double ReserveFraction { get; init; }
    /// <summary>Additional spinning reserve requirement [MW].</summary>
    public double ReserveMw { get; init; }
    /// <summary>Number of linear segments approximating a quadratic cost between Pmin and Pmax.</summary>
    public int CostSegments { get; init; } = 4;
    /// <summary>Length of one period [h] (scales ramp limits and energy costs; prices stay in $/MWh).</summary>
    public double PeriodHours { get; init; } = 1;
    /// <summary>Enforce DC branch limits (PTDF) when a network is given.</summary>
    public bool EnforceNetwork { get; init; } = true;
    /// <summary>Multiplier on RateA for base-case limits.</summary>
    public double RatingFactor { get; init; } = 1;
    /// <summary>Multiplier on RateA for post-contingency limits.</summary>
    public double EmergencyRatingFactor { get; init; } = 1;
    /// <summary>Overrides RateA per branch index [MW].</summary>
    public IReadOnlyDictionary<int, double>? RatingOverrides { get; init; }
    /// <summary>Branch indices whose outage is secured (N-1 via LODF).</summary>
    public IReadOnlyList<int> Contingencies { get; init; } = [];
    /// <summary>Add branch constraints only when violated and re-solve (constraint generation); false = add all upfront.</summary>
    public bool LazyNetworkConstraints { get; init; } = true;
    /// <summary>Maximum constraint-generation rounds.</summary>
    public int MaxNetworkIterations { get; init; } = 20;
    /// <summary>MILP solver (default: built-in <see cref="BranchAndBoundSolver"/>).</summary>
    public IMilpSolver? Solver { get; init; }
    /// <summary>LP solver for the fixed-commitment pricing run (default: <see cref="Solver"/> if it is an <see cref="ILpSolver"/>, else <see cref="SimplexSolver"/>).</summary>
    public ILpSolver? LpSolver { get; init; }
    public SolverOptions SolverOptions { get; init; } = new() { RelativeGap = 1e-5 };
    /// <summary>Re-solve the LP with the commitment fixed to obtain LMPs from its duals.</summary>
    public bool ComputePrices { get; init; } = true;

    public static ScucOptions Default { get; } = new();

    /// <summary>Base rating [MW] of branch l: the override if present, else RateA (before the rating factors).</summary>
    public double Rating(PowerNetwork net, int l) =>
        RatingOverrides is not null && RatingOverrides.TryGetValue(l, out var r) ? r : net.Branches[l].RateA;
}

/// <summary>DC decomposition of locational marginal prices per bus [$/MWh]: LMP = energy + congestion + loss (loss = 0 in a lossless DC model).
/// Energy is the price at the reference (slack) bus; congestion = −Σ_c μ_c·PTDF_c,i over binding branch constraints.</summary>
public sealed record DcLmpComponents(int[] BusIds, double[] Lmp, double[] Energy, double[] Congestion, double[] Loss)
{
    public double LmpAt(int busId) => Lmp[Array.IndexOf(BusIds, busId)];
}

/// <summary>A binding network or reserve constraint of a market / SCUC solution.</summary>
/// <param name="Kind">"Flow", "ContingencyFlow" or "Reserve".</param>
/// <param name="Hour">Period index.</param>
/// <param name="Branch">Monitored branch index (−1 for reserve).</param>
/// <param name="Contingency">Outaged branch index (−1 for base case).</param>
/// <param name="ShadowPrice">Cost reduction per MW of relaxation [$/MWh] (≥ 0).</param>
/// <param name="FlowMw">Flow in the constrained direction [MW] (reserve: scheduled reserve).</param>
/// <param name="LimitMw">Limit [MW] (reserve: requirement).</param>
public sealed record MarketConstraint(string Kind, int Hour, int Branch, int Contingency, double ShadowPrice, double FlowMw, double LimitMw);

/// <summary>Result of <see cref="SecurityConstrainedUnitCommitment"/>. Arrays are indexed [unit, period] / [bus position, period] / [branch, period].</summary>
public sealed class ScucResult
{
    public required SolveStatus Status { get; init; }
    public required IReadOnlyList<ScucUnit> Units { get; init; }
    public required bool[,] Commitment { get; init; }
    public required bool[,] Startup { get; init; }
    public required bool[,] Shutdown { get; init; }
    /// <summary>Dispatch [MW] (from the pricing LP when prices were computed).</summary>
    public required double[,] DispatchMw { get; init; }
    /// <summary>Cleared price-responsive demand per bid block [MW] (market clearing).</summary>
    public required double[] ElasticDemandMw { get; init; }
    /// <summary>MILP objective [$] (production + no-load + startup − price-responsive demand benefit).</summary>
    public required double Objective { get; init; }
    /// <summary>Energy + no-load cost under the piecewise-linear cost model [$].</summary>
    public required double ProductionCost { get; init; }
    /// <summary>Startup cost [$].</summary>
    public required double StartupCost { get; init; }
    public double TotalCost => ProductionCost + StartupCost;
    public required double MipGap { get; init; }
    /// <summary>Bus ids aligned with the first index of <see cref="Lmp"/> (single entry 0 without a network).</summary>
    public required int[] BusIds { get; init; }
    /// <summary>LMP [$/MWh] per [bus position, period] (NaN when prices were not computed).</summary>
    public required double[,] Lmp { get; init; }
    /// <summary>System energy price per period [$/MWh] (reference-bus LMP).</summary>
    public required double[] SystemLambda { get; init; }
    public required DcLmpComponents[] LmpComponents { get; init; }
    /// <summary>Base-case branch flows [MW] per [branch, period] (empty without a network).</summary>
    public required double[,] FlowsMw { get; init; }
    /// <summary>Committed capacity minus dispatch per period [MW].</summary>
    public required double[] SpinningReserveMw { get; init; }
    public required IReadOnlyList<MarketConstraint> Binding { get; init; }
    /// <summary>Constraint-generation rounds and network rows in the final model.</summary>
    public required int NetworkIterations { get; init; }
    public required int NetworkConstraints { get; init; }
    public required LinearSolution MilpSolution { get; init; }
    public LinearSolution? PricingSolution { get; init; }

    public int UnitCount => Commitment.GetLength(0);
    public int Periods => Commitment.GetLength(1);
    public bool HasSolution => MilpSolution.HasSolution;
    public double LmpAt(int busId, int period) => Lmp[Array.IndexOf(BusIds, busId), period];
}
