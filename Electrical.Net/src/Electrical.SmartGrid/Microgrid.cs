using Electrical.Renewable;

namespace Electrical.SmartGrid;

/// <summary>
/// Microgrid data for 24 h (or any horizon) energy management. Units are consistent kW / kWh / $ (the battery model is unit-agnostic:
/// give its capacity in kWh and power in kW).
/// </summary>
public sealed record MicrogridConfig
{
    public required double[] LoadKw { get; init; }
    public required double[] PvKw { get; init; }
    public double StepHours { get; init; } = 1;
    public required BatteryStorage Battery { get; init; }
    public double DieselMaxKw { get; init; } = 0;
    /// <summary>Minimum stable loading as a fraction of the rating when running.</summary>
    public double DieselMinLoading { get; init; } = 0.3;
    /// <summary>Fuel cost per kWh produced [$/kWh] (linear part).</summary>
    public double DieselCostPerKwh { get; init; } = 0.30;
    /// <summary>No-load cost while running [$/h].</summary>
    public double DieselNoLoadCostPerHour { get; init; } = 2;
    /// <summary>Grid purchase price per step [$/kWh].</summary>
    public required double[] BuyPrice { get; init; }
    /// <summary>Grid feed-in price per step [$/kWh].</summary>
    public required double[] SellPrice { get; init; }
    public double GridImportLimitKw { get; init; } = double.PositiveInfinity;
    public double GridExportLimitKw { get; init; } = double.PositiveInfinity;
    /// <summary>Penalty for unserved energy [$/kWh] (value of lost load).</summary>
    public double UnservedPenalty { get; init; } = 5;
    /// <summary>Battery wear cost per kWh of discharge [$/kWh].</summary>
    public double BatteryWearCostPerKwh { get; init; } = 0.02;
    /// <summary>Require the final SoC to be at least the initial SoC (optimal dispatch only).</summary>
    public bool KeepFinalSoc { get; init; } = true;

    public int Steps => LoadKw.Length;

    /// <summary>
    /// A demonstration microgrid: 24 h residential/commercial load (peak 400 kW), 500 kWp PV on a clear day, 600 kWh / 250 kW battery,
    /// 300 kW diesel, time-of-use grid tariff (0.08 / 0.15 / 0.28 $/kWh off-peak / shoulder / peak 17–22 h), feed-in 0.05 $/kWh.
    /// </summary>
    public static MicrogridConfig Demo()
    {
        double[] load = new double[24], pv = new double[24], buy = new double[24], sell = new double[24];
        for (int h = 0; h < 24; h++)
        {
            load[h] = 220 + 120 * Math.Exp(-Math.Pow((h - 19.5) / 2.5, 2)) + 60 * Math.Exp(-Math.Pow((h - 11) / 3.5, 2));
            pv[h] = h is >= 6 and <= 18 ? 500 * 0.85 * Math.Pow(Math.Sin(Math.PI * (h - 6 + 0.5) / 13), 1.5) : 0;
            buy[h] = h is >= 17 and < 22 ? 0.28 : h is >= 7 and < 17 ? 0.15 : 0.08;
            sell[h] = 0.05;
        }
        return new MicrogridConfig
        {
            LoadKw = load, PvKw = pv, BuyPrice = buy, SellPrice = sell, Battery = new BatteryStorage(600, 250, 0.5) { SocMin = 0.1, SocMax = 0.95 },
            DieselMaxKw = 300, GridImportLimitKw = 350, GridExportLimitKw = 200,
        };
    }
}

/// <summary>Microgrid dispatch schedule (kW per step; battery positive = discharge, grid positive = import).</summary>
public sealed record MicrogridDispatch(string Strategy, double[] BatteryKw, double[] DieselKw, double[] GridKw, double[] CurtailedKw, double[] UnservedKw,
    double[] Soc, double[] StepCost, double TotalCost)
{
    public double ImportKwh(double stepHours = 1) => GridKw.Where(g => g > 0).Sum() * stepHours;
    public double ExportKwh(double stepHours = 1) => -GridKw.Where(g => g < 0).Sum() * stepHours;
    public double DieselKwh(double stepHours = 1) => DieselKw.Sum() * stepHours;
}

/// <summary>Microgrid energy management: rule-based (load following) and optimal dynamic programming over battery SoC.</summary>
public static class MicrogridEms
{
    /// <summary>
    /// Rule-based dispatch: PV serves load; surplus charges the battery, then is exported (then curtailed). Deficit is covered by the
    /// battery, then by the cheaper of grid and diesel (diesel respecting its minimum loading), then unserved.
    /// </summary>
    public static MicrogridDispatch RuleBased(MicrogridConfig c)
    {
        int n = c.Steps; double dt = c.StepHours;
        var b = c.Battery.Clone();
        var pb = new double[n]; var pd = new double[n]; var pg = new double[n]; var cur = new double[n]; var uns = new double[n];
        var soc = new double[n]; var cost = new double[n];
        for (int t = 0; t < n; t++)
        {
            double net = c.LoadKw[t] - c.PvKw[t];
            pb[t] = b.Step(net, dt); // surplus (net < 0) charges, deficit discharges
            double residual = net - pb[t];
            (pd[t], pg[t], cur[t], uns[t], double sc) = Supply(c, t, residual);
            cost[t] = sc + c.BatteryWearCostPerKwh * Math.Max(0, pb[t]) * dt;
            soc[t] = b.Soc;
        }
        return new MicrogridDispatch("RuleBased", pb, pd, pg, cur, uns, soc, cost, cost.Sum());
    }

    /// <summary>
    /// Optimal dispatch by backward dynamic programming over <paramref name="socLevels"/> discrete SoC states between SocMin and SocMax.
    /// For each transition the battery power follows from the energy change and efficiencies, and the residual demand is covered at least
    /// cost by grid and diesel (static sub-problem); optional terminal constraint SoC_end ≥ SoC_0.
    /// </summary>
    public static MicrogridDispatch Optimal(MicrogridConfig c, int socLevels = 101)
    {
        int n = c.Steps; double dt = c.StepHours;
        var bat = c.Battery;
        double cap = bat.EffectiveCapacityMwh; // numerically kWh in this context
        var levels = Enumerable.Range(0, socLevels).Select(i => bat.SocMin + (bat.SocMax - bat.SocMin) * i / (socLevels - 1)).ToArray();
        int start = 0;
        for (int i = 1; i < socLevels; i++) if (Math.Abs(levels[i] - bat.Soc) < Math.Abs(levels[start] - bat.Soc)) start = i;
        var value = new double[n + 1, socLevels];
        var next = new int[n, socLevels];
        for (int j = 0; j < socLevels; j++) value[n, j] = c.KeepFinalSoc && j < start ? double.PositiveInfinity : 0;
        for (int t = n - 1; t >= 0; t--)
        {
            double net = c.LoadKw[t] - c.PvKw[t];
            for (int i = 0; i < socLevels; i++)
            {
                double best = double.PositiveInfinity; int arg = i;
                for (int j = 0; j < socLevels; j++)
                {
                    if (double.IsPositiveInfinity(value[t + 1, j])) continue;
                    double p = BatteryPower(levels[i], levels[j], cap, bat, dt);
                    if (double.IsNaN(p)) continue;
                    var (_, _, _, _, sc) = Supply(c, t, net - p);
                    double v = sc + c.BatteryWearCostPerKwh * Math.Max(0, p) * dt + value[t + 1, j];
                    if (v < best) { best = v; arg = j; }
                }
                value[t, i] = best; next[t, i] = arg;
            }
        }
        var pb = new double[n]; var pd = new double[n]; var pg = new double[n]; var cur = new double[n]; var uns = new double[n];
        var soc = new double[n]; var cost = new double[n];
        int s = start;
        for (int t = 0; t < n; t++)
        {
            int j = next[t, s];
            pb[t] = BatteryPower(levels[s], levels[j], cap, bat, dt);
            if (double.IsNaN(pb[t])) pb[t] = 0;
            (pd[t], pg[t], cur[t], uns[t], double sc) = Supply(c, t, c.LoadKw[t] - c.PvKw[t] - pb[t]);
            cost[t] = sc + c.BatteryWearCostPerKwh * Math.Max(0, pb[t]) * dt;
            soc[t] = levels[j];
            s = j;
        }
        return new MicrogridDispatch("DynamicProgramming", pb, pd, pg, cur, uns, soc, cost, cost.Sum());
    }

    // grid-side battery power for an SoC transition, NaN when infeasible
    private static double BatteryPower(double s0, double s1, double cap, BatteryStorage b, double dt)
    {
        double dE = (s1 - s0) * cap;
        double p = dE < 0 ? -dE * b.DischargeEfficiency / dt : -dE / b.ChargeEfficiency / dt;
        if (p > b.MaxDischargeMw + 1e-9 || -p > b.MaxChargeMw + 1e-9) return double.NaN;
        return p;
    }

    /// <summary>Least-cost supply of a residual demand r [kW] (negative = surplus) from grid and diesel for step t.</summary>
    internal static (double Diesel, double Grid, double Curtailed, double Unserved, double Cost) Supply(MicrogridConfig c, int t, double r)
    {
        double dt = c.StepHours;
        if (r <= 0)
        {
            double export = Math.Min(-r, c.GridExportLimitKw);
            return (0, -export, -r - export, 0, -c.SellPrice[t] * export * dt);
        }
        double dmin = c.DieselMinLoading * c.DieselMaxKw;
        var candidates = new List<double> { 0 };
        if (c.DieselMaxKw > 0)
        {
            candidates.Add(Math.Clamp(r - c.GridImportLimitKw, dmin, c.DieselMaxKw));
            candidates.Add(Math.Clamp(r, dmin, c.DieselMaxKw));
            candidates.Add(c.DieselMaxKw);
        }
        (double, double, double, double, double) best = default;
        double bestCost = double.PositiveInfinity;
        foreach (double d in candidates)
        {
            double g = r - d; // >0 import, <0 surplus from diesel min loading
            double imp = Math.Min(Math.Max(g, 0), c.GridImportLimitKw);
            double uns = Math.Max(0, g - imp);
            double exp = Math.Min(Math.Max(-g, 0), c.GridExportLimitKw);
            double curt = Math.Max(0, -g - exp);
            double cost = (d > 0 ? c.DieselNoLoadCostPerHour * dt + c.DieselCostPerKwh * d * dt : 0) + c.BuyPrice[t] * imp * dt
                          - c.SellPrice[t] * exp * dt + c.UnservedPenalty * uns * dt;
            if (cost < bestCost - 1e-12) { bestCost = cost; best = (d, imp - exp, curt, uns, cost); }
        }
        return best;
    }
}

/// <summary>Kind of distributed energy resource in a virtual power plant.</summary>
public enum DerKind { Solar, Wind, Battery, FlexibleLoad, Chp, Diesel, Other }

/// <summary>
/// A DER in a VPP. Generation convention: positive = injection. For flexible loads use negative ranges (e.g. Pmin = −2, Pmax = 0 means up to
/// 2 MW consumption that can be curtailed). Variable renewables scale Pmax by <see cref="AvailabilityPu"/> per step.
/// </summary>
public sealed record DerResource(string Name, DerKind Kind, double PminMw, double PmaxMw, double MarginalCost, double[]? AvailabilityPu = null,
    BatteryStorage? Battery = null)
{
    public (double Min, double Max) Range(int step, double stepHours)
    {
        if (Battery is not null) return (-Battery.AvailableChargeMw(stepHours), Battery.AvailableDischargeMw(stepHours));
        double a = AvailabilityPu is null ? 1 : AvailabilityPu[Math.Min(step, AvailabilityPu.Length - 1)];
        return (Math.Min(PminMw, PmaxMw * a), PmaxMw * a);
    }
}

/// <summary>Aggregated flexibility envelope [MW] per step: the VPP can be dispatched anywhere in [MinMw, MaxMw].</summary>
public sealed record FlexibilityEnvelope(double[] MinMw, double[] MaxMw)
{
    public double[] RangeMw => MinMw.Zip(MaxMw, (a, b) => b - a).ToArray();
}

/// <summary>One step of a VPP supply offer: <paramref name="QuantityMw"/> offered at <paramref name="PricePerMwh"/>.</summary>
public sealed record BidStep(string Resource, double PricePerMwh, double QuantityMw, double CumulativeMw);

/// <summary>
/// Virtual power plant: aggregates DER limits into a flexibility envelope, builds a merit-order (stepwise) bidding curve and dispatches a
/// target set-point in merit order.
/// </summary>
public sealed class VirtualPowerPlant
{
    public List<DerResource> Resources { get; } = [];

    public VirtualPowerPlant Add(DerResource r) { Resources.Add(r); return this; }

    /// <summary>Aggregated min/max output per step.</summary>
    public FlexibilityEnvelope Envelope(int steps, double stepHours = 1)
    {
        var mn = new double[steps]; var mx = new double[steps];
        for (int t = 0; t < steps; t++)
            foreach (var r in Resources) { var (a, b) = r.Range(t, stepHours); mn[t] += a; mx[t] += b; }
        return new FlexibilityEnvelope(mn, mx);
    }

    /// <summary>Merit-order offer curve above the aggregated minimum at a step (ascending price).</summary>
    public IReadOnlyList<BidStep> BidCurve(int step = 0, double stepHours = 1)
    {
        var list = new List<BidStep>();
        double cum = Resources.Sum(r => r.Range(step, stepHours).Min);
        foreach (var r in Resources.OrderBy(r => r.MarginalCost).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            var (a, b) = r.Range(step, stepHours);
            if (b - a <= 1e-12) continue;
            cum += b - a;
            list.Add(new BidStep(r.Name, r.MarginalCost, b - a, cum));
        }
        return list;
    }

    /// <summary>Dispatches the VPP to <paramref name="targetMw"/> (clipped to the envelope) in merit order. Returns set-points [MW] and cost [$/h].</summary>
    public (Dictionary<string, double> SetpointsMw, double CostPerHour, double DeliveredMw) Dispatch(double targetMw, int step = 0, double stepHours = 1)
    {
        var sp = new Dictionary<string, double>();
        double total = 0;
        foreach (var r in Resources) { var (a, _) = r.Range(step, stepHours); sp[r.Name] = a; total += a; }
        foreach (var r in Resources.OrderBy(r => r.MarginalCost).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            if (total >= targetMw - 1e-12) break;
            var (a, b) = r.Range(step, stepHours);
            double inc = Math.Min(b - a, targetMw - total);
            sp[r.Name] += inc; total += inc;
        }
        double cost = Resources.Sum(r => r.MarginalCost * sp[r.Name]);
        return (sp, cost, total);
    }
}
