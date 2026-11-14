using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Electrical.Core;
using Electrical.Core.Network;
using Electrical.DigitalTwin;
using Electrical.Forecasting;
using Electrical.Optimization;
using Electrical.PowerFlow;
using Electrical.Renewable;
using Electrical.ShortCircuit;
using Electrical.Stability;

namespace Electrical.AI;

/// <summary>What the <see cref="GridAgent"/> operates on.</summary>
public sealed class GridAgentContext
{
    /// <summary>Default study case name (case9, case14, case33bw, syntheticN) used when a question names none.</summary>
    public string CaseName { get; set; } = "case14";
    /// <summary>Optional custom network; when set it is used instead of <see cref="CaseName"/> for questions that name no case.</summary>
    public PowerNetwork? Network { get; set; }
    /// <summary>Optional live digital twin (status, explained alarms, asset health).</summary>
    public GridTwin? Twin { get; set; }

    /// <summary>A fresh copy of the requested (or default) network.</summary>
    public PowerNetwork Resolve(string? caseName)
    {
        if (string.IsNullOrWhiteSpace(caseName)) return Network?.Clone() ?? TestCases.ByName(CaseName);
        return TestCases.ByName(caseName);
    }
}

/// <summary>
/// Grounded power-system tools exposed to <see cref="GridAgent"/>. All numbers come from the Electrical.Net solvers; outputs are plain text
/// in English or Bahasa Indonesia. Units: MW, Mvar, MVA, kA, pu, %.
/// </summary>
public static class GridTools
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string CaseProp = "\"case\":{\"type\":\"string\",\"description\":\"Study case: case9, case14, case33bw or syntheticN. Omit for the operator's default network.\"}";

    public static IReadOnlyList<AgentTool> Create(GridAgentContext ctx, Func<string> language)
    {
        bool Id() => language() == "id";
        string T(string en, string id) => Id() ? id : en;
        static string F(double v, int d = 2) => v.ToString("N" + d, Inv);
        static string? S(JsonObject a, string k) => a[k] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')) : null;
        static double? D(JsonObject a, string k)
        {
            if (a[k] is not JsonValue v) return null;
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, Inv, out var p) ? p : null;
        }
        PowerNetwork Net(JsonObject a) => ctx.Resolve(S(a, "case"));
        string CaseLabel(JsonObject a) => S(a, "case") ?? (ctx.Network?.Name ?? ctx.CaseName);
        Task<string> Done(string s) => Task.FromResult(s);

        return
        [
            new("grid_overview", "Summary of the operator's network: buses, branches, generators, total load and generation capacity.",
                Schema(CaseProp), (a, _) =>
                {
                    var n = Net(a);
                    return Done(T($"{n.Name}: {n.BusCount} buses, {n.Branches.Count} branches, {n.Generators.Count} generators, {n.Loads.Count} loads; total load {F(n.TotalLoadMw)} MW / {F(n.TotalLoadMvar)} Mvar, generation capacity {F(n.TotalGenerationCapacityMw)} MW, base {n.BaseMva} MVA, {n.FrequencyHz} Hz. {ElectricalInfo.CreditEn}.",
                                  $"{n.Name}: {n.BusCount} bus, {n.Branches.Count} cabang, {n.Generators.Count} pembangkit, {n.Loads.Count} beban; total beban {F(n.TotalLoadMw)} MW / {F(n.TotalLoadMvar)} Mvar, kapasitas pembangkitan {F(n.TotalGenerationCapacityMw)} MW, basis {n.BaseMva} MVA, {n.FrequencyHz} Hz. {ElectricalInfo.CreditId}."));
                }),

            new("run_power_flow", "AC Newton-Raphson power flow. Returns convergence, losses, min/max voltage, most loaded branch and limit violations.",
                Schema(CaseProp + ""","load_scale":{"type":"number","description":"Multiplier for all loads (1 = base case, 1.2 = +20%)."}"""), (a, _) =>
                {
                    var n = Net(a);
                    double k = D(a, "load_scale") ?? 1;
                    if (k != 1) new ScaleLoad(k).Apply(n);
                    BranchRatings.AssignMissing(n);
                    var r = PowerFlowSolver.Solve(n);
                    if (!r.Converged) return Done(T($"Power flow for {CaseLabel(a)} at load ×{F(k)} did NOT converge (beyond the loadability limit?).",
                                                     $"Aliran daya {CaseLabel(a)} pada beban ×{F(k)} TIDAK konvergen (melewati batas pembebanan?)."));
                    var kp = GridKpis.From(r);
                    return Done(T($"Power flow {CaseLabel(a)} (load ×{F(k)}): converged in {r.Iterations} iterations. Load {F(r.TotalLoadMw)} MW, generation {F(r.TotalGenerationMw)} MW, losses {F(r.LossesMw, 3)} MW ({F(100 * r.LossesMw / Math.Max(r.TotalLoadMw, 1e-9))}%). Vmin {F(kp.MinVoltagePu, 4)} pu at bus {kp.MinVoltageBus}, Vmax {F(kp.MaxVoltagePu, 4)} pu at bus {kp.MaxVoltageBus}. Most loaded branch {kp.MostLoadedBranch} at {F(kp.MaxLoadingPercent, 1)}% (unrated branches rated at 125% of base flow). Violations (0.95–1.05 pu, 100%): {kp.VoltageViolations} voltage, {kp.Overloads} overload.",
                                  $"Aliran daya {CaseLabel(a)} (beban ×{F(k)}): konvergen dalam {r.Iterations} iterasi. Beban {F(r.TotalLoadMw)} MW, pembangkitan {F(r.TotalGenerationMw)} MW, rugi-rugi {F(r.LossesMw, 3)} MW ({F(100 * r.LossesMw / Math.Max(r.TotalLoadMw, 1e-9))}%). Vmin {F(kp.MinVoltagePu, 4)} pu di bus {kp.MinVoltageBus}, Vmax {F(kp.MaxVoltagePu, 4)} pu di bus {kp.MaxVoltageBus}. Cabang terberat {kp.MostLoadedBranch} pada {F(kp.MaxLoadingPercent, 1)}% (cabang tanpa rating diberi rating 125% aliran dasar). Pelanggaran (0,95–1,05 pu, 100%): {kp.VoltageViolations} tegangan, {kp.Overloads} beban lebih."));
                }),

            new("contingency_n1", "N-1 branch contingency screening (AC). Returns the most severe outages with max loading and minimum voltage.",
                Schema(CaseProp + ""","top":{"type":"integer","description":"Number of worst contingencies to list (default 5)."}"""), (a, _) =>
                {
                    var n = Net(a);
                    BranchRatings.AssignMissing(n);
                    int top = (int)(D(a, "top") ?? 5);
                    var res = ContingencyAnalysis.RunN1(n);
                    var sb = new StringBuilder(T($"N-1 screening of {CaseLabel(a)}: {res.Count} outages, {res.Count(r => r.Islanding)} cause islanding, {res.Count(r => r.Overloads > 0)} cause overloads, {res.Count(r => r.VoltageViolations > 0)} cause voltage violations. Worst:",
                                                 $"Analisis N-1 {CaseLabel(a)}: {res.Count} kontingensi, {res.Count(r => r.Islanding)} menyebabkan pulau, {res.Count(r => r.Overloads > 0)} menyebabkan beban lebih, {res.Count(r => r.VoltageViolations > 0)} menyebabkan pelanggaran tegangan. Terburuk:"));
                    foreach (var r in res.Take(top))
                        sb.Append('\n').Append(r.Islanding ? T($"- outage {r.Outage}: islanding (loss of supply)", $"- padam {r.Outage}: terbentuk pulau (kehilangan suplai)")
                            : !r.Converged ? T($"- outage {r.Outage}: power flow diverges", $"- padam {r.Outage}: aliran daya divergen")
                            : T($"- outage {r.Outage}: max loading {F(r.MaxLoadingPercent, 1)}% on {r.MostLoadedBranch}, Vmin {F(r.MinVoltage, 4)} pu at bus {r.MinVoltageBus}",
                                $"- padam {r.Outage}: pembebanan maks {F(r.MaxLoadingPercent, 1)}% pada {r.MostLoadedBranch}, Vmin {F(r.MinVoltage, 4)} pu di bus {r.MinVoltageBus}"));
                    return Done(sb.ToString());
                }),

            new("short_circuit", "IEC 60909 short-circuit current at a bus (maximum, c = 1.1). Fault types: 3ph, slg, ll, dlg.",
                Schema(CaseProp + ""","bus":{"type":"integer","description":"Faulted bus id."},"fault_type":{"type":"string","enum":["3ph","slg","ll","dlg"]}"""), (a, _) =>
                {
                    var n = Net(a);
                    int bus = (int)(D(a, "bus") ?? n.Buses.First(b => b.Type == BusType.PQ).Id);
                    var type = ParseFaultType(S(a, "fault_type"));
                    var r = ShortCircuitAnalysis.Calculate(n, bus, type);
                    return Done(T($"Short circuit ({type}) at bus {bus} of {CaseLabel(a)} ({F(r.BaseKv, 1)} kV): I\"k = {F(r.IkKa, 3)} kA, peak ip = {F(r.IpKa, 3)} kA (κ = {F(r.Kappa, 3)}), breaking Ib = {F(r.IbKa, 3)} kA, S\"k = {F(r.SkMva, 1)} MVA, R/X = {F(r.RxRatio, 3)}" + (r.EarthCurrentKa > 1e-9 ? $", earth current {F(r.EarthCurrentKa, 3)} kA." : "."),
                                  $"Hubung singkat ({type}) di bus {bus} {CaseLabel(a)} ({F(r.BaseKv, 1)} kV): I\"k = {F(r.IkKa, 3)} kA, arus puncak ip = {F(r.IpKa, 3)} kA (κ = {F(r.Kappa, 3)}), arus pemutusan Ib = {F(r.IbKa, 3)} kA, S\"k = {F(r.SkMva, 1)} MVA, R/X = {F(r.RxRatio, 3)}" + (r.EarthCurrentKa > 1e-9 ? $", arus tanah {F(r.EarthCurrentKa, 3)} kA." : ".")));
                }),

            new("economic_dispatch", "Economic dispatch of the generators (lambda iteration, optionally with B-coefficient losses).",
                Schema(CaseProp + ""","with_losses":{"type":"boolean","description":"Include transmission losses (B-coefficients from AC power flow)."}"""), (a, _) =>
                {
                    var n = Net(a);
                    bool losses = a["with_losses"] is JsonValue wl && wl.TryGetValue<bool>(out var b) && b;
                    var r = losses ? EconomicDispatch.SolveWithLosses(n) : EconomicDispatch.Solve(n);
                    var units = DispatchUnit.FromNetwork(n);
                    var sb = new StringBuilder(T($"Economic dispatch {CaseLabel(a)}{(losses ? " with losses" : "")}: λ = {F(r.Lambda)} $/MWh, total cost {F(r.TotalCost)} $/h, generation {F(r.TotalGenerationMw)} MW, losses {F(r.LossesMw, 3)} MW.",
                                                 $"Dispatch ekonomis {CaseLabel(a)}{(losses ? " dengan rugi-rugi" : "")}: λ = {F(r.Lambda)} $/MWh, biaya total {F(r.TotalCost)} $/jam, pembangkitan {F(r.TotalGenerationMw)} MW, rugi-rugi {F(r.LossesMw, 3)} MW."));
                    for (int i = 0; i < units.Count; i++) sb.Append($"\n- {units[i].Name}: {F(r.OutputMw[i])} MW [{F(units[i].PminMw, 0)}–{F(units[i].PmaxMw, 0)}]");
                    return Done(sb.ToString());
                }),

            new("voltage_stability_margin", "Voltage-stability margin from a PV curve (continuation power flow) and the L-index.",
                Schema(CaseProp), (a, _) =>
                {
                    var n = Net(a);
                    var pv = VoltageStability.PvCurve(n);
                    var li = VoltageStability.LIndex(n);
                    return Done(T($"Voltage stability {CaseLabel(a)}: maximum loadability λmax = {F(pv.LambdaMax, 3)} (margin {F(100 * pv.MarginPu, 1)}% = {F(pv.MarginMw, 1)} MW), weakest bus {pv.CriticalBusId}. L-index max {F(li.Max, 3)} at bus {li.CriticalBusId} (0 = stiff, 1 = collapse).",
                                  $"Stabilitas tegangan {CaseLabel(a)}: pembebanan maksimum λmax = {F(pv.LambdaMax, 3)} (margin {F(100 * pv.MarginPu, 1)}% = {F(pv.MarginMw, 1)} MW), bus terlemah {pv.CriticalBusId}. Indeks-L maks {F(li.Max, 3)} di bus {li.CriticalBusId} (0 = kaku, 1 = runtuh)."));
                }),

            new("hosting_capacity", "PV hosting capacity at a bus (overvoltage 1.05 pu or thermal limit).",
                Schema(CaseProp + ""","bus":{"type":"integer","description":"Bus id (default: the weakest bus)."},"max_mw":{"type":"number","description":"Search limit in MW."}"""), (a, _) =>
                {
                    var n = Net(a);
                    BranchRatings.AssignMissing(n);
                    var basePf = PowerFlowSolver.Solve(n);
                    int bus = (int)(D(a, "bus") ?? basePf.MinVoltageBus.BusId);
                    double maxMw = D(a, "max_mw") ?? Math.Max(1, n.TotalLoadMw * 2);
                    double vmax = Math.Max(1.05, basePf.Buses.Max(b => b.Vm) + 0.01);
                    var r = HostingCapacity.Analyze(n, bus, maxMw / 40, maxMw, vmax);
                    string lim = r.LimitingFactor switch { "Overvoltage" => T("overvoltage", "tegangan lebih"), "Thermal" => T("thermal overload", "beban lebih termal"), "NonConvergence" => T("non-convergence", "tidak konvergen"), _ => T("none up to the search limit", "tidak ada hingga batas pencarian") };
                    return Done(T($"PV hosting capacity at bus {bus} of {CaseLabel(a)}: {F(r.HostingCapacityMw, 3)} MW (limit: {lim}; voltage limit {F(vmax, 3)} pu, loading limit 100%).",
                                  $"Kapasitas penampung PV di bus {bus} {CaseLabel(a)}: {F(r.HostingCapacityMw, 3)} MW (pembatas: {lim}; batas tegangan {F(vmax, 3)} pu, batas pembebanan 100%)."));
                }),

            new("forecast_load", "Short-term load forecast (Holt-Winters on 14 days of synthetic history of the network load). Returns peak, minimum and energy.",
                Schema(CaseProp + ""","hours":{"type":"integer","description":"Horizon in hours (default 24)."},"method":{"type":"string","enum":["holtwinters","regression","similarday","ensemble"]}"""), (a, _) =>
                {
                    var n = Net(a);
                    int hours = Math.Clamp((int)(D(a, "hours") ?? 24), 1, 24 * 14);
                    var method = (S(a, "method") ?? "holtwinters").ToLowerInvariant() switch
                    {
                        "regression" => LoadForecastMethod.Regression, "similarday" => LoadForecastMethod.SimilarDay, "ensemble" => LoadForecastMethod.Ensemble,
                        _ => LoadForecastMethod.HoltWinters,
                    };
                    var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
                    var hist = SyntheticProfiles.Load(LoadProfileKind.Residential, start, 14, TimeSpan.FromHours(1), n.TotalLoadMw, noise: 0.02, seed: 11);
                    var fc = LoadForecast.Forecast(hist, hours, method);
                    int ip = Array.IndexOf(fc.Values, fc.Max());
                    return Done(T($"Load forecast {CaseLabel(a)} ({method}, next {hours} h from {fc.Start:yyyy-MM-dd HH:mm}): peak {F(fc.Max())} MW at {fc.TimeAt(ip):ddd HH:mm}, minimum {F(fc.Min())} MW, energy {F(fc.Energy())} MWh, load factor {F(fc.LoadFactor(), 3)}.",
                                  $"Prakiraan beban {CaseLabel(a)} ({method}, {hours} jam ke depan dari {fc.Start:yyyy-MM-dd HH:mm}): puncak {F(fc.Max())} MW pada {fc.TimeAt(ip):ddd HH:mm}, minimum {F(fc.Min())} MW, energi {F(fc.Energy())} MWh, faktor beban {F(fc.LoadFactor(), 3)}."));
                }),

            new("explain_alarms", "Explains current limit violations (undervoltage, overvoltage, overloads) with their causes. Uses the live digital twin when attached, else a power flow.",
                Schema(CaseProp + ""","load_scale":{"type":"number","description":"Load multiplier for the power-flow fallback."}"""), (a, _) =>
                {
                    string lang = Id() ? "id" : "en";
                    IReadOnlyList<ExplainedAlarm> alarms;
                    string src;
                    if (ctx.Twin?.Current is { Converged: true } st && S(a, "case") is null)
                    {
                        alarms = st.Alarms; src = T($"digital twin cycle {st.Step}", $"siklus digital twin {st.Step}");
                    }
                    else
                    {
                        var n = Net(a);
                        double k = D(a, "load_scale") ?? 1;
                        if (k != 1) new ScaleLoad(k).Apply(n);
                        BranchRatings.AssignMissing(n);
                        alarms = AlarmExplainer.Explain(PowerFlowSolver.Solve(n), context: new AlarmContext(LoadScale: k));
                        src = T($"power flow of {CaseLabel(a)} at load ×{F(k)}", $"aliran daya {CaseLabel(a)} pada beban ×{F(k)}");
                    }
                    if (alarms.Count == 0) return Done(T($"No alarms ({src}): all voltages within 0.95–1.05 pu and loadings below 90%.", $"Tidak ada alarm ({src}): semua tegangan dalam 0,95–1,05 pu dan pembebanan di bawah 90%."));
                    var sb = new StringBuilder(T($"{alarms.Count} alarm(s) ({src}):", $"{alarms.Count} alarm ({src}):"));
                    foreach (var al in alarms.OrderByDescending(x => x.Severity).Take(10)) sb.Append($"\n- [{al.Severity}] {al.Localized(lang)}");
                    if (alarms.Count > 10) sb.Append(T($"\n… and {alarms.Count - 10} more.", $"\n… dan {alarms.Count - 10} lainnya."));
                    return Done(sb.ToString());
                }),

            new("twin_status", "Status of the live digital twin: last estimate, bad data, forecast deviation, alarms and transformer/line health (IEEE C57.91 ageing).",
                Schema(""), (_, _) =>
                {
                    if (ctx.Twin is null) return Done(T("No digital twin is attached to this assistant.", "Tidak ada digital twin yang terhubung ke asisten ini."));
                    var sb = new StringBuilder(ctx.Twin.Summary(Id() ? "id" : "en"));
                    foreach (var h in ctx.Twin.AssetHealth.OrderBy(x => x.HealthIndex).Take(3))
                        sb.Append(T($"\n- {h.Name}: {F(h.LoadingPercent, 0)}% load, hot spot {F(h.HotSpotC, 1)} °C, F_AA {F(h.AgingFactor, 3)}, health {F(h.HealthIndex, 0)} ({h.Status})",
                                    $"\n- {h.Name}: beban {F(h.LoadingPercent, 0)}%, titik panas {F(h.HotSpotC, 1)} °C, F_AA {F(h.AgingFactor, 3)}, kesehatan {F(h.HealthIndex, 0)} ({h.StatusId})"));
                    return Done(sb.ToString());
                }),
        ];
    }

    /// <summary>Maps "3ph", "slg", "ll", "dlg" (and synonyms) to <see cref="FaultType"/>.</summary>
    public static FaultType ParseFaultType(string? s) => (s ?? "3ph").ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "") switch
    {
        "slg" or "lg" or "singlelinetoground" or "1ph" or "ag" => FaultType.SingleLineToGround,
        "ll" or "linetoline" or "2ph" or "bc" => FaultType.LineToLine,
        "dlg" or "llg" or "doublelinetoground" or "2phg" or "bcg" => FaultType.DoubleLineToGround,
        _ => FaultType.ThreePhase,
    };

    private static string Schema(string properties) => "{\"type\":\"object\",\"properties\":{" + properties + "}}";
}
