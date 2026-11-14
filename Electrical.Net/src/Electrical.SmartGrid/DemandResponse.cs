using Electrical.Renewable;

namespace Electrical.SmartGrid;

/// <summary>Result of a demand-response programme. Powers in MW (or any consistent unit), prices per MWh.</summary>
/// <param name="OriginalMw">Baseline demand.</param>
/// <param name="NewMw">Demand after response.</param>
/// <param name="PeakReductionPercent">(peak₀ − peak₁)/peak₀ × 100.</param>
/// <param name="EnergyChangeMwh">Σ(new − original)·Δt.</param>
/// <param name="CostBefore">Σ price·demand·Δt for the baseline.</param>
/// <param name="CostAfter">Σ price·demand·Δt after response.</param>
public sealed record DemandResponseResult(double[] OriginalMw, double[] NewMw, double PeakReductionPercent, double EnergyChangeMwh, double CostBefore, double CostAfter);

/// <summary>Battery peak-shaving simulation. Net = load − battery (battery positive = discharge).</summary>
public sealed record PeakShavingResult(double[] LoadMw, double[] BatteryMw, double[] NetMw, double[] Soc, double ThresholdMw, double OriginalPeakMw,
    double NewPeakMw, bool ThresholdMet);

/// <summary>Price-based demand response and battery peak shaving.</summary>
public static class DemandResponse
{
    /// <summary>
    /// Price-responsive load with an elasticity matrix (Kirschen et al. 2000; Aalami et al. 2010):
    /// d_t = d0_t·[1 + E_tt·(ρ_t − ρ0)/ρ0 + Σ_{s≠t} E_ts·(ρ_s − ρ0)/ρ0] with self-elasticity E_tt (≤ 0) and uniform cross-elasticity
    /// E_ts (≥ 0, load shifting). ρ0 is the reference (flat) price, the time-average of <paramref name="price"/> by default.
    /// With <paramref name="conserveEnergy"/> the response is rescaled to pure shifting (same total energy).
    /// </summary>
    public static DemandResponseResult PriceBased(IReadOnlyList<double> loadMw, IReadOnlyList<double> price, double selfElasticity = -0.1,
        double crossElasticity = 0.01, double stepHours = 1, double? referencePrice = null, bool conserveEnergy = false)
    {
        int n = loadMw.Count;
        if (price.Count != n) throw new ArgumentException("Price and load must be aligned.");
        double rho0 = referencePrice ?? price.Average();
        var rel = price.Select(p => (p - rho0) / rho0).ToArray();
        double sumRel = rel.Sum();
        var d = new double[n];
        for (int t = 0; t < n; t++)
            d[t] = Math.Max(0, loadMw[t] * (1 + selfElasticity * rel[t] + crossElasticity * (sumRel - rel[t])));
        if (conserveEnergy)
        {
            double k = loadMw.Sum() / Math.Max(d.Sum(), 1e-12);
            for (int t = 0; t < n; t++) d[t] *= k;
        }
        return Summarize(loadMw.ToArray(), d, price, stepHours);
    }

    /// <summary>
    /// Battery peak shaving: discharge whenever load exceeds <paramref name="thresholdMw"/>, recharge below it without pushing the net
    /// demand above the threshold. The battery object is not modified (a clone is simulated).
    /// </summary>
    public static PeakShavingResult PeakShaving(IReadOnlyList<double> loadMw, BatteryStorage battery, double thresholdMw, double stepHours = 1)
    {
        var b = battery.Clone();
        int n = loadMw.Count;
        var pb = new double[n]; var net = new double[n]; var soc = new double[n];
        for (int t = 0; t < n; t++)
        {
            double l = loadMw[t];
            pb[t] = l > thresholdMw ? b.Step(l - thresholdMw, stepHours) : b.Step(-(thresholdMw - l), stepHours);
            net[t] = l - pb[t];
            soc[t] = b.Soc;
        }
        double peak0 = loadMw.Max(), peak1 = net.Max();
        return new PeakShavingResult(loadMw.ToArray(), pb, net, soc, thresholdMw, peak0, peak1, peak1 <= thresholdMw + 1e-6);
    }

    /// <summary>Lowest threshold [MW] the battery can hold over the profile (bisection on <see cref="PeakShaving"/>).</summary>
    public static PeakShavingResult OptimalPeakShaving(IReadOnlyList<double> loadMw, BatteryStorage battery, double stepHours = 1)
    {
        double lo = loadMw.Average(), hi = loadMw.Max();
        for (int it = 0; it < 50 && hi - lo > 1e-6 * hi; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (PeakShaving(loadMw, battery, mid, stepHours).ThresholdMet) hi = mid; else lo = mid;
        }
        return PeakShaving(loadMw, battery, hi, stepHours);
    }

    internal static DemandResponseResult Summarize(double[] original, double[] d, IReadOnlyList<double> price, double stepHours)
    {
        double p0 = original.Max(), p1 = d.Max();
        double c0 = 0, c1 = 0;
        for (int t = 0; t < d.Length; t++) { c0 += price[t] * original[t] * stepHours; c1 += price[t] * d[t] * stepHours; }
        return new DemandResponseResult(original, d, p0 > 0 ? 100 * (p0 - p1) / p0 : 0, (d.Sum() - original.Sum()) * stepHours, c0, c1);
    }
}

/// <summary>An EV charging session. Steps index the scheduling horizon; the car is available on [Arrival, Departure).</summary>
/// <param name="EnergyKwh">Energy to deliver [kWh].</param>
/// <param name="MaxPowerKw">Charger / on-board limit [kW].</param>
/// <param name="Feeder">Feeder (or transformer) the charger is connected to.</param>
public sealed record EvSession(string Id, int ArrivalStep, int DepartureStep, double EnergyKwh, double MaxPowerKw, int Feeder = 0);

/// <summary>EV schedule. PowerKw is indexed [session, step].</summary>
public sealed record EvScheduleResult(double[,] PowerKw, double[] EvLoadKw, double[] TotalLoadKw, double PeakKw, double[] UnmetKwh, string Strategy)
{
    public double TotalUnmetKwh => UnmetKwh.Sum();
    public double LoadFactor => TotalLoadKw.Average() / Math.Max(PeakKw, 1e-12);
}

/// <summary>EV charging strategies: uncontrolled (as soon as plugged in) and smart valley filling.</summary>
public static class EvCharging
{
    /// <summary>Charges every car at full power from arrival until its energy is delivered (or it leaves).</summary>
    public static EvScheduleResult Uncontrolled(IReadOnlyList<EvSession> sessions, IReadOnlyList<double> baseLoadKw, double stepHours = 1)
    {
        int T = baseLoadKw.Count;
        var p = new double[sessions.Count, T];
        var unmet = new double[sessions.Count];
        for (int s = 0; s < sessions.Count; s++)
        {
            var e = sessions[s];
            double rem = e.EnergyKwh;
            for (int t = Math.Max(0, e.ArrivalStep); t < Math.Min(T, e.DepartureStep) && rem > 1e-9; t++)
            {
                double kw = Math.Min(e.MaxPowerKw, rem / stepHours);
                p[s, t] = kw;
                rem -= kw * stepHours;
            }
            unmet[s] = Math.Max(0, rem);
        }
        return Build(p, baseLoadKw, unmet, "Uncontrolled");
    }

    /// <summary>
    /// Smart charging by greedy valley filling: sessions are processed earliest-departure first; each session's energy is placed in small
    /// chunks into the available step with the lowest total (base + EV) load, respecting the charger limit, optional per-feeder EV capacity
    /// [kW] and an overall transformer capacity [kW] for base + EV load.
    /// </summary>
    public static EvScheduleResult ValleyFilling(IReadOnlyList<EvSession> sessions, IReadOnlyList<double> baseLoadKw, double stepHours = 1,
        IReadOnlyDictionary<int, double>? feederCapacityKw = null, double transformerCapacityKw = double.PositiveInfinity, double chunkKwh = 0.25)
    {
        int T = baseLoadKw.Count;
        var p = new double[sessions.Count, T];
        var total = baseLoadKw.ToArray();
        var feederLoad = new Dictionary<int, double[]>();
        var unmet = new double[sessions.Count];
        var order = Enumerable.Range(0, sessions.Count).OrderBy(i => sessions[i].DepartureStep).ThenBy(i => sessions[i].ArrivalStep).ToArray();
        foreach (int s in order)
        {
            var e = sessions[s];
            if (!feederLoad.TryGetValue(e.Feeder, out var fl)) feederLoad[e.Feeder] = fl = new double[T];
            double cap = feederCapacityKw is not null && feederCapacityKw.TryGetValue(e.Feeder, out var c) ? c : double.PositiveInfinity;
            double rem = e.EnergyKwh;
            int a = Math.Max(0, e.ArrivalStep), d = Math.Min(T, e.DepartureStep);
            while (rem > 1e-9)
            {
                int best = -1; double bestLoad = double.PositiveInfinity, bestRoom = 0;
                for (int t = a; t < d; t++)
                {
                    double room = Math.Min(e.MaxPowerKw - p[s, t], Math.Min(cap - fl[t], transformerCapacityKw - total[t]));
                    if (room <= 1e-9) continue;
                    if (total[t] < bestLoad - 1e-12) { bestLoad = total[t]; best = t; bestRoom = room; }
                }
                if (best < 0) break;
                double kw = Math.Min(bestRoom, Math.Min(chunkKwh, rem) / stepHours);
                p[s, best] += kw; total[best] += kw; fl[best] += kw;
                rem -= kw * stepHours;
            }
            unmet[s] = Math.Max(0, rem);
        }
        return Build(p, baseLoadKw, unmet, "ValleyFilling");
    }

    /// <summary>Deterministic synthetic residential EV fleet: evening arrivals (17–21 h), morning departures (6–8 h next day) on hourly steps of a 24 h horizon starting at 12:00.</summary>
    public static List<EvSession> SyntheticFleet(int count, int seed = 21, int feeders = 1)
    {
        var rng = new Random(seed);
        var list = new List<EvSession>(count);
        for (int i = 0; i < count; i++)
        {
            int arr = 5 + rng.Next(0, 5);       // 17:00–21:00 when step 0 = 12:00
            int dep = 18 + rng.Next(0, 3);      // 06:00–08:00
            double kwh = Math.Round(8 + rng.NextDouble() * 22, 1);
            double kw = rng.NextDouble() < 0.7 ? 7.4 : 11;
            list.Add(new EvSession($"EV{i + 1}", arr, dep, kwh, kw, i % Math.Max(1, feeders)));
        }
        return list;
    }

    private static EvScheduleResult Build(double[,] p, IReadOnlyList<double> baseLoad, double[] unmet, string strategy)
    {
        int T = baseLoad.Count;
        var ev = new double[T]; var tot = new double[T];
        for (int t = 0; t < T; t++)
        {
            for (int s = 0; s < p.GetLength(0); s++) ev[t] += p[s, t];
            tot[t] = baseLoad[t] + ev[t];
        }
        return new EvScheduleResult(p, ev, tot, tot.Max(), unmet, strategy);
    }
}
