using Electrical.Core.Network;

namespace Electrical.Optimization;

/// <summary>Thermal unit for unit commitment. Cost C(P) = C2·P² + C1·P + C0 [$/h] (C0 = no-load cost while on).</summary>
/// <param name="StartupCost">Cost of one start [$].</param>
/// <param name="MinUpHours">Minimum up time [h].</param>
/// <param name="MinDownHours">Minimum down time [h].</param>
/// <param name="InitialHours">Initial status: +h = on for h hours, −h = off for h hours.</param>
public sealed record UcUnit(string Name, PolynomialCost Cost, double PminMw, double PmaxMw, double StartupCost = 0, int MinUpHours = 1,
    int MinDownHours = 1, int InitialHours = -24)
{
    /// <summary>Full-load average cost C(Pmax)/Pmax [$/MWh] used for the priority list.</summary>
    public double FullLoadAverageCost => Cost.Evaluate(PmaxMw) / PmaxMw;

    internal DispatchUnit ToDispatch() => new(Name, Cost, PminMw, PmaxMw);
}

/// <summary>Unit commitment schedule. Index [unit, hour].</summary>
public sealed class UnitCommitmentResult
{
    public required bool[,] Status { get; init; }
    public required double[,] DispatchMw { get; init; }
    /// <summary>System λ per hour [$/MWh].</summary>
    public required double[] Lambda { get; init; }
    public required double ProductionCost { get; init; }
    public required double StartupCost { get; init; }
    public double TotalCost => ProductionCost + StartupCost;
    /// <summary>Unit indices sorted by full-load average cost (cheapest first).</summary>
    public required int[] PriorityOrder { get; init; }
    /// <summary>Committed capacity minus demand per hour [MW].</summary>
    public required double[] SpinningReserveMw { get; init; }
    public required bool Feasible { get; init; }

    public int Units => Status.GetLength(0);
    public int Hours => Status.GetLength(1);
    public int StartsOf(int unit)
    {
        int s = 0;
        for (int t = 1; t < Hours; t++) if (Status[unit, t] && !Status[unit, t - 1]) s++;
        return s;
    }
}

/// <summary>
/// Unit commitment by forward dynamic programming over priority-list states (Wood &amp; Wollenberg, "Power Generation, Operation and Control",
/// §4.2): at each hour the candidate states are "the k cheapest units (by full-load average cost) are on", k = 1..N, plus the initial state.
/// Each state is dispatched economically (λ-iteration); transitions add start-up costs and are rejected if they violate a unit's minimum
/// up/down time along the surviving path. A spinning reserve requirement (fraction of demand and/or MW) limits the feasible states.
/// </summary>
public static class UnitCommitment
{
    public static UnitCommitmentResult Solve(IReadOnlyList<UcUnit> units, IReadOnlyList<double> demandMw, double reserveFraction = 0.1,
        double reserveMw = 0)
    {
        int nu = units.Count, T = demandMw.Count;
        var order = Enumerable.Range(0, nu).OrderBy(i => units[i].FullLoadAverageCost).ToArray();
        // states: k = 0..nu (prefix of the priority order); state index nu+1 = initial set (only at t = −1)
        var stateSets = new List<bool[]>();
        for (int k = 0; k <= nu; k++)
        {
            var on = new bool[nu];
            for (int j = 0; j < k; j++) on[order[j]] = true;
            stateSets.Add(on);
        }
        var init = units.Select(u => u.InitialHours > 0).ToArray();

        // DP node: cost, back pointer, per-unit consecutive on(+)/off(−) hours along the path
        var cost = new double[T, nu + 1];
        var back = new int[T, nu + 1];
        var run = new int[T, nu + 1][];
        var prod = new double[T, nu + 1];
        var lam = new double[T, nu + 1];
        var disp = new double[T, nu + 1][];
        for (int t = 0; t < T; t++)
        {
            double need = demandMw[t] * (1 + reserveFraction) + reserveMw;
            for (int k = 0; k <= nu; k++)
            {
                cost[t, k] = double.PositiveInfinity;
                var on = stateSets[k];
                double cap = 0, pmin = 0;
                for (int i = 0; i < nu; i++) if (on[i]) { cap += units[i].PmaxMw; pmin += units[i].PminMw; }
                if (cap < need - 1e-9 || pmin > demandMw[t] + 1e-9 || cap < demandMw[t] - 1e-9) continue;
                var idx = Enumerable.Range(0, nu).Where(i => on[i]).ToArray();
                var ed = EconomicDispatch.Solve(idx.Select(i => units[i].ToDispatch()).ToList(), demandMw[t]);
                var p = new double[nu];
                for (int j = 0; j < idx.Length; j++) p[idx[j]] = ed.OutputMw[j];
                prod[t, k] = ed.TotalCost;
                lam[t, k] = ed.Lambda;
                disp[t, k] = p;

                int prevCount = t == 0 ? 1 : nu + 1;
                for (int pk = 0; pk < prevCount; pk++)
                {
                    bool[] prevOn; int[] prevRun; double prevCost;
                    if (t == 0) { prevOn = init; prevRun = units.Select(u => u.InitialHours).ToArray(); prevCost = 0; }
                    else
                    {
                        if (double.IsPositiveInfinity(cost[t - 1, pk])) continue;
                        prevOn = stateSets[pk]; prevRun = run[t - 1, pk]; prevCost = cost[t - 1, pk];
                    }
                    double su = 0;
                    bool ok = true;
                    for (int i = 0; i < nu && ok; i++)
                    {
                        if (on[i] && !prevOn[i])
                        {
                            if (-prevRun[i] < units[i].MinDownHours) ok = false;
                            su += units[i].StartupCost;
                        }
                        else if (!on[i] && prevOn[i] && prevRun[i] < units[i].MinUpHours) ok = false;
                    }
                    if (!ok) continue;
                    double c = prevCost + su + prod[t, k];
                    if (c < cost[t, k])
                    {
                        cost[t, k] = c;
                        back[t, k] = pk;
                        var r = new int[nu];
                        for (int i = 0; i < nu; i++)
                            r[i] = on[i] ? (prevOn[i] ? Math.Max(prevRun[i], 0) + 1 : 1) : (prevOn[i] ? -1 : Math.Min(prevRun[i], 0) - 1);
                        run[t, k] = r;
                    }
                }
            }
        }
        // best terminal state
        int best = -1;
        double bestCost = double.PositiveInfinity;
        for (int k = 0; k <= nu; k++) if (cost[T - 1, k] < bestCost) { bestCost = cost[T - 1, k]; best = k; }
        var status = new bool[nu, T];
        var dispatch = new double[nu, T];
        var lambda = new double[T];
        var reserve = new double[T];
        if (best < 0)
            return new UnitCommitmentResult
            {
                Status = status, DispatchMw = dispatch, Lambda = lambda, ProductionCost = double.NaN, StartupCost = double.NaN,
                PriorityOrder = order, SpinningReserveMw = reserve, Feasible = false,
            };
        var path = new int[T];
        for (int t = T - 1, k = best; t >= 0; t--) { path[t] = k; k = back[t, k]; }
        double production = 0, startup = 0;
        for (int t = 0; t < T; t++)
        {
            var on = stateSets[path[t]];
            var prevOn = t == 0 ? init : stateSets[path[t - 1]];
            for (int i = 0; i < nu; i++)
            {
                status[i, t] = on[i];
                dispatch[i, t] = disp[t, path[t]][i];
                if (on[i] && !prevOn[i]) startup += units[i].StartupCost;
                if (on[i]) reserve[t] += units[i].PmaxMw;
            }
            reserve[t] -= demandMw[t];
            production += prod[t, path[t]];
            lambda[t] = lam[t, path[t]];
        }
        return new UnitCommitmentResult
        {
            Status = status, DispatchMw = dispatch, Lambda = lambda, ProductionCost = production, StartupCost = startup,
            PriorityOrder = order, SpinningReserveMw = reserve, Feasible = true,
        };
    }

    /// <summary>
    /// The three-unit example of Wood &amp; Wollenberg (Example 3A/4A, fuel costs 1.1/1.0/1.2 $/MBtu applied):
    /// unit 1 150–600 MW, unit 2 100–400 MW, unit 3 50–200 MW. Priority order by full-load average cost: 2, 1, 3
    /// (9.40, 9.79, 11.19 $/MWh).
    /// </summary>
    public static IReadOnlyList<UcUnit> WoodWollenbergThreeUnits() =>
    [
        new("Unit 1", new PolynomialCost(0.00142 * 1.1, 7.2 * 1.1, 510 * 1.1), 150, 600, StartupCost: 800, MinUpHours: 4, MinDownHours: 4, InitialHours: 8),
        new("Unit 2", new PolynomialCost(0.00194, 7.85, 310), 100, 400, StartupCost: 500, MinUpHours: 3, MinDownHours: 3, InitialHours: 8),
        new("Unit 3", new PolynomialCost(0.00482 * 1.2, 7.97 * 1.2, 78 * 1.2), 50, 200, StartupCost: 100, MinUpHours: 1, MinDownHours: 1, InitialHours: -5),
    ];
}
