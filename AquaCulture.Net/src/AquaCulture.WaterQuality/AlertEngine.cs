using AquaCulture.Core;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;

namespace AquaCulture.WaterQuality;

public enum AlertSeverity { Info, Warning, Critical }

public enum Comparison { Below, Above }

/// <summary>Raised or cleared water-quality alert.</summary>
public sealed record WaterAlert(
    DateTime Timestamp, string PondId, WaterParameter Parameter, AlertSeverity Severity, double Value, double Threshold,
    Text2 Message, bool Cleared = false, string RuleId = "") : IAquaEvent;

/// <summary>A threshold rule with hysteresis.</summary>
public sealed record AlertRule(string Id, WaterParameter Parameter, Comparison Comparison, double Threshold, AlertSeverity Severity, double Hysteresis = 0)
{
    /// <summary>Evaluate on un-ionised ammonia (NH₃-N) rather than TAN.</summary>
    public bool OnUnionizedAmmonia { get; init; }
}

/// <summary>
/// Stateful alert engine. Each (pond, rule) raises once when its threshold is crossed and clears only after the value
/// recovers past the hysteresis band, so a noisy sensor near a limit does not flood operators. Also detects a fast
/// night-time DO decline (trend rule). Optionally publishes <see cref="WaterAlert"/> on an <see cref="IAquaEventBus"/>.
/// </summary>
public sealed class AlertEngine
{
    private readonly Dictionary<(string Pond, string Rule), WaterAlert> _active = [];
    private readonly Dictionary<string, List<(DateTime T, double Do)>> _doHistory = [];
    private readonly IAquaEventBus? _bus;

    public AlertEngine(IEnumerable<AlertRule> rules, IAquaEventBus? bus = null) { Rules = rules.ToList(); _bus = bus; }

    public List<AlertRule> Rules { get; }

    /// <summary>DO decline (mg/L per hour) that triggers a trend warning.</summary>
    public double DoDeclineRatePerHour { get; set; } = 0.6;

    public IReadOnlyCollection<WaterAlert> Active => _active.Values;

    /// <summary>Default rules derived from a species profile.</summary>
    public static IEnumerable<AlertRule> DefaultRules(SpeciesProfile s) =>
    [
        new("do-low", WaterParameter.DissolvedOxygen, Comparison.Below, s.DoOptimal, AlertSeverity.Warning, 0.3),
        new("do-critical", WaterParameter.DissolvedOxygen, Comparison.Below, s.DoCritical, AlertSeverity.Critical, 0.3),
        new("temp-high", WaterParameter.Temperature, Comparison.Above, s.Temperature.OptHigh, AlertSeverity.Warning, 0.3),
        new("temp-critical-high", WaterParameter.Temperature, Comparison.Above, s.Temperature.Max - 1, AlertSeverity.Critical, 0.3),
        new("temp-low", WaterParameter.Temperature, Comparison.Below, s.Temperature.OptLow, AlertSeverity.Warning, 0.3),
        new("ph-low", WaterParameter.Ph, Comparison.Below, s.Ph.OptLow, AlertSeverity.Warning, 0.1),
        new("ph-high", WaterParameter.Ph, Comparison.Above, s.Ph.OptHigh, AlertSeverity.Warning, 0.1),
        new("nh3-high", WaterParameter.Ammonia, Comparison.Above, s.Nh3Max, AlertSeverity.Warning, s.Nh3Max * 0.1) { OnUnionizedAmmonia = true },
        new("nh3-critical", WaterParameter.Ammonia, Comparison.Above, s.Nh3Max * 2.5, AlertSeverity.Critical, s.Nh3Max * 0.2) { OnUnionizedAmmonia = true },
        new("no2-high", WaterParameter.Nitrite, Comparison.Above, s.NitriteMax, AlertSeverity.Warning, s.NitriteMax * 0.1),
        new("sal-low", WaterParameter.Salinity, Comparison.Below, s.Salinity.OptLow, AlertSeverity.Warning, 0.5),
        new("sal-high", WaterParameter.Salinity, Comparison.Above, s.Salinity.OptHigh, AlertSeverity.Warning, 0.5),
        new("alk-low", WaterParameter.Alkalinity, Comparison.Below, s.Alkalinity.OptLow, AlertSeverity.Info, 5),
        new("turb-high", WaterParameter.Turbidity, Comparison.Above, s.TurbidityMax, AlertSeverity.Info, 5),
    ];

    public static AlertEngine ForSpecies(SpeciesProfile s, IAquaEventBus? bus = null) => new(DefaultRules(s), bus);

    /// <summary>Evaluates a sample and returns alerts raised or cleared by it.</summary>
    public IReadOnlyList<WaterAlert> Evaluate(WaterSample sample)
    {
        var changes = new List<WaterAlert>();
        foreach (var rule in Rules)
        {
            double? v = sample[rule.Parameter];
            if (rule.OnUnionizedAmmonia)
                v = v is { } tan && sample.TemperatureC is { } t && sample.Ph is { } ph ? WaterChemistry.UnionizedAmmonia(tan, t, ph, sample.SalinityPpt ?? 0) : null;
            if (v is not { } value) continue;
            var key = (sample.PondId, rule.Id);
            bool breached = rule.Comparison == Comparison.Below ? value < rule.Threshold : value > rule.Threshold;
            bool recovered = rule.Comparison == Comparison.Below ? value >= rule.Threshold + rule.Hysteresis : value <= rule.Threshold - rule.Hysteresis;
            if (!_active.ContainsKey(key) && breached)
            {
                var a = new WaterAlert(sample.Timestamp, sample.PondId, rule.Parameter, rule.Severity, value, rule.Threshold, Message(rule, value), false, rule.Id);
                _active[key] = a; changes.Add(a);
            }
            else if (_active.TryGetValue(key, out var open) && recovered)
            {
                _active.Remove(key);
                changes.Add(open with { Timestamp = sample.Timestamp, Value = value, Cleared = true,
                    Message = new($"Recovered: {open.Message.En}", $"Pulih: {open.Message.Id}") });
            }
        }
        if (sample.DissolvedOxygen is { } dox) TrendCheck(sample, dox, changes);
        foreach (var c in changes) _bus?.Publish(c);
        return changes;
    }

    private void TrendCheck(WaterSample s, double dox, List<WaterAlert> changes)
    {
        if (!_doHistory.TryGetValue(s.PondId, out var h)) _doHistory[s.PondId] = h = [];
        h.Add((s.Timestamp, dox));
        h.RemoveAll(x => (s.Timestamp - x.T).TotalHours > 2);
        if (h.Count < 3) return;
        double hours = (h[^1].T - h[0].T).TotalHours;
        if (hours < 0.75) return;
        // least-squares slope over the last two hours (robust to a single noisy reading)
        double rate = Core.Data.Stats.LinearRegression(h.Select(x => (x.T - h[0].T).TotalHours).ToArray(), h.Select(x => x.Do).ToArray()).Slope;
        var key = (s.PondId, "do-trend");
        if (rate <= -DoDeclineRatePerHour && !_active.ContainsKey(key))
        {
            var a = new WaterAlert(s.Timestamp, s.PondId, WaterParameter.DissolvedOxygen, AlertSeverity.Warning, dox, -DoDeclineRatePerHour,
                new($"DO falling {-rate:0.00} mg/L per hour — start aerators before dawn", $"DO turun {-rate:0.00} mg/L per jam — nyalakan aerator sebelum subuh"), false, "do-trend");
            _active[key] = a; changes.Add(a);
        }
        else if (rate > -DoDeclineRatePerHour / 3 && _active.Remove(key, out var open))
            changes.Add(open with { Timestamp = s.Timestamp, Value = dox, Cleared = true, Message = new("DO decline stopped", "Penurunan DO berhenti") });
    }

    private static Text2 Message(AlertRule r, double v)
    {
        var info = WaterParameters.Info[r.Parameter];
        string name = r.OnUnionizedAmmonia ? "NH₃" : info.Name.En, nameId = r.OnUnionizedAmmonia ? "NH₃" : info.Name.Id;
        string unit = r.OnUnionizedAmmonia ? "mg/L" : info.Unit;
        string fmt = r.OnUnionizedAmmonia ? "0.000" : "0.##";
        string dirEn = r.Comparison == Comparison.Below ? "below" : "above", dirId = r.Comparison == Comparison.Below ? "di bawah" : "di atas";
        return new($"{name} {v.ToString(fmt)} {unit} {dirEn} {r.Threshold.ToString(fmt)}", $"{nameId} {v.ToString(fmt)} {unit} {dirId} {r.Threshold.ToString(fmt)}");
    }
}
