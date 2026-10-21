namespace Blazor.AI.ThreeD;

/// <summary>A decision lever with its range, unit and display format.</summary>
public sealed record StrategyLever(string Name, double Min, double Max, string Unit, string Format = "0.##");

/// <summary>A named scenario (point on the two levers) shown as a tower.</summary>
public sealed record StrategyScenario(string Name, double A, double B);

/// <summary>
/// A strategy model: two levers, one KPI, and a stochastic single-trial simulator
/// <c>(a, b, volatility, scale, rng) → KPI</c> evaluated by Monte-Carlo.
/// </summary>
public sealed record StrategyPreset(
    string Key,
    string Name,
    string Kpi,
    string KpiUnit,
    StrategyLever A,
    StrategyLever B,
    Func<double, double, double, double, Random, double> Trial,
    IReadOnlyList<StrategyScenario> Scenarios,
    string Context)
{
    public static IReadOnlyList<StrategyPreset> All { get; } = [Banking(), Insurance(), Healthcare(), SupplyChain(), Retail()];

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>Consumer lending: loan rate vs. credit-score cut-off → annual profit (billion IDR).</summary>
    public static StrategyPreset Banking() => new("banking", "Banking · consumer loans", "Annual profit", "B IDR",
        new("Loan interest rate", 8, 24, "%", "0.0"), new("Credit-score cut-off", 520, 760, "pts", "0"),
        (rate, cutoff, vol, scale, rng) =>
        {
            var applicants = 2000 * scale * Math.Exp(-(rate - 8) / 14) * (1 + Gauss(rng) * 0.1 * vol);
            var approved = applicants * Math.Clamp(1 - (cutoff - 500) / 330, 0.05, 1);
            var pd = Math.Clamp(0.22 * Math.Exp(-(cutoff - 520) / 90) * (1 + rate / 40) * (1 + Gauss(rng) * 0.35 * vol), 0.002, 0.6);
            var loan = 0.05; // billion IDR average
            return approved * loan * (rate / 100 - 0.06) * (1 - pd) - approved * loan * pd * 0.55;
        },
        [new("Conservative", 11, 700), new("Balanced", 15, 640), new("Aggressive", 21, 560)],
        "Higher rates earn more per loan but drive applicants away and attract riskier borrowers; a stricter cut-off lowers defaults but shrinks volume.");

    /// <summary>Motor insurance: premium vs. deductible → underwriting profit (billion IDR).</summary>
    public static StrategyPreset Insurance() => new("insurance", "Insurance · motor policies", "Underwriting profit", "B IDR",
        new("Annual premium", 2.0, 6.0, "M IDR", "0.0"), new("Deductible", 0, 5, "M IDR", "0.0"),
        (premium, deductible, vol, scale, rng) =>
        {
            var retention = Math.Clamp(1.1 - premium / 7 - deductible / 18, 0.15, 1);
            var policies = 10000 * scale * retention;
            var claimRate = 0.11 * (1 - deductible / 12) * (1 + Gauss(rng) * 0.25 * vol);
            var severity = Math.Max(0, 18 - deductible) * (1 + Gauss(rng) * 0.3 * vol);
            return policies * (premium - claimRate * severity - 0.6) / 1000;
        },
        [new("Low price", 2.6, 1), new("Market", 3.8, 2.5), new("Premium", 5.2, 1)],
        "Higher premiums and deductibles cut claims cost per policy but lose customers to competitors.");

    /// <summary>Clinic operations: nurses per shift vs. appointment slots → net outcome score.</summary>
    public static StrategyPreset Healthcare() => new("healthcare", "Healthcare · outpatient clinic", "Net outcome score", "pts",
        new("Nurses per shift", 4, 20, "nurses", "0"), new("Appointment slots / hour", 6, 30, "slots", "0"),
        (nurses, slots, vol, scale, rng) =>
        {
            var arrivals = slots * 8 * scale * (1 + Gauss(rng) * 0.15 * vol);
            var capacity = nurses * 2.4 * 8;
            var utilisation = arrivals / capacity;
            var wait = utilisation < 1 ? 10 * utilisation / (1 - utilisation + 0.05) : 180 * utilisation;
            var treated = Math.Min(arrivals, capacity);
            return treated * 1.0 - Math.Min(wait, 240) * 0.8 - nurses * 6.0;
        },
        [new("Lean", 7, 14), new("Balanced", 12, 20), new("Expanded", 17, 26)],
        "More slots treat more patients but queues explode once demand exceeds nursing capacity; more nurses cost money.");

    /// <summary>Inventory: reorder point vs. order quantity → total cost (negated, so higher is better).</summary>
    public static StrategyPreset SupplyChain() => new("supply", "Supply chain · warehouse stock", "Negative total cost", "M IDR",
        new("Reorder point", 20, 200, "units", "0"), new("Order quantity", 50, 600, "units", "0"),
        (reorder, quantity, vol, scale, rng) =>
        {
            double stock = reorder + quantity, cost = 0, pipeline = 0;
            var leadLeft = 0;
            for (var day = 0; day < 60; day++)
            {
                var demand = Math.Max(0, 20 * scale * (1 + Gauss(rng) * 0.45 * vol));
                if (leadLeft > 0 && --leadLeft == 0)
                {
                    stock += pipeline;
                    pipeline = 0;
                }

                var sold = Math.Min(stock, demand);
                stock -= sold;
                cost += (demand - sold) * 0.9 + stock * 0.02;
                if (stock <= reorder && pipeline == 0)
                {
                    pipeline = quantity;
                    leadLeft = 5;
                    cost += 25;
                }
            }

            return -cost;
        },
        [new("Just-in-time", 50, 120), new("Balanced", 110, 260), new("Safety stock", 180, 500)],
        "Low stock risks stock-outs (lost margin); high stock ties up cash and storage; each order has a fixed cost.");

    /// <summary>Retail campaign: discount vs. marketing spend → campaign profit (M IDR).</summary>
    public static StrategyPreset Retail() => new("retail", "Retail · seasonal campaign", "Campaign profit", "M IDR",
        new("Discount", 0, 40, "%", "0"), new("Marketing spend", 0, 400, "M IDR", "0"),
        (discount, spend, vol, scale, rng) =>
        {
            var reach = 1 - Math.Exp(-spend / 160);
            var uplift = 1 + 2.2 * discount / 40 * (0.4 + reach) + Gauss(rng) * 0.2 * vol;
            var units = 4000 * scale * Math.Max(0.2, uplift);
            var margin = 150 * (1 - discount / 100) - 95;
            return units * margin / 1000 - spend;
        },
        [new("Brand-safe", 5, 80), new("Balanced", 15, 180), new("Blowout", 32, 350)],
        "Discounts lift volume but erode margin; marketing extends reach with diminishing returns.");
}

/// <summary>Monte-Carlo summary for one lever combination.</summary>
public sealed record StrategyCell(double A, double B, double Mean, double P5, double P95);

/// <summary>Grid evaluation of a <see cref="StrategyPreset"/>.</summary>
public static class StrategySimulation
{
    public static StrategyCell Evaluate(StrategyPreset p, double a, double b, double volatility, double scale, int trials, int seed)
    {
        var rng = new Random(seed);
        var v = new double[trials];
        for (var i = 0; i < trials; i++)
        {
            v[i] = p.Trial(a, b, volatility, scale, rng);
        }

        Array.Sort(v);
        return new StrategyCell(a, b, v.Average(), v[(int)(0.05 * (trials - 1))], v[(int)(0.95 * (trials - 1))]);
    }

    public static StrategyCell[,] Grid(StrategyPreset p, int n, double volatility, double scale, int trials)
    {
        var grid = new StrategyCell[n, n];
        Parallel.For(0, n * n, k =>
        {
            int i = k / n, j = k % n;
            var a = p.A.Min + (p.A.Max - p.A.Min) * j / (n - 1);
            var b = p.B.Min + (p.B.Max - p.B.Min) * i / (n - 1);
            grid[i, j] = Evaluate(p, a, b, volatility, scale, trials, 1000 + k);
        });
        return grid;
    }
}
