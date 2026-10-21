namespace Blazor.AI.ThreeD;

/// <summary>A live metric with history, a target and a simple anomaly flag (|z| over the recent window).</summary>
public sealed class LiveMetric(string name, string unit, double baseline, double noise, double target, bool higherIsBetter = true)
{
    private readonly Queue<double> _history = new();

    public string Name { get; } = name;
    public string Unit { get; } = unit;
    public double Target { get; set; } = target;
    public bool HigherIsBetter { get; } = higherIsBetter;
    public double Baseline { get; set; } = baseline;
    public double Noise { get; } = noise;
    public double Value { get; private set; } = baseline;

    /// <summary>Additive drift applied each step (set to simulate an incident).</summary>
    public double Drift { get; set; }

    public IReadOnlyCollection<double> History => _history;

    public double Z
    {
        get
        {
            if (_history.Count < 8)
            {
                return 0;
            }

            var mean = _history.Average();
            var sd = Math.Sqrt(_history.Average(v => (v - mean) * (v - mean)));
            return sd < 1e-9 ? 0 : (Value - mean) / sd;
        }
    }

    public bool Anomalous => Math.Abs(Z) > 2.6;

    /// <summary>Attainment against target in [0, 1.5].</summary>
    public double Attainment => Target == 0 ? 1 : Math.Clamp(HigherIsBetter ? Value / Target : Target / Math.Max(1e-9, Value), 0, 1.5);

    public double Change => _history.Count < 2 ? 0 : (Value - _history.First()) / Math.Max(1e-9, Math.Abs(_history.First()));

    public void Step(Random rng)
    {
        Baseline += Drift;
        Value = Math.Max(0, Baseline + (rng.NextDouble() * 2 - 1) * Noise);
        _history.Enqueue(Value);
        if (_history.Count > 60)
        {
            _history.Dequeue();
        }
    }

    public string Format() => $"{Value:0.##} {Unit}".Trim();
}

/// <summary>Seeded generator of enterprise operations data for the premium 3D dashboards.</summary>
public static class EnterpriseSim
{
    public static List<LiveMetric> ExecutiveKpis() =>
    [
        new("Revenue", "Rp bn", 128, 3, 124),
        new("Gross margin", "%", 41, 0.8, 40),
        new("Cash runway", "months", 19, 0.2, 18),
        new("NPS", "", 46, 2, 44),
        new("Churn", "%", 2.4, 0.15, 2.0, higherIsBetter: false),
        new("On-time delivery", "%", 93, 1.2, 92),
        new("Inventory days", "days", 38, 1.5, 32, higherIsBetter: false),
        new("Employee engagement", "%", 74, 1, 75),
        new("Carbon intensity", "tCO₂/Rp bn", 0.82, 0.03, 0.7, higherIsBetter: false),
        new("Pipeline", "Rp bn", 410, 9, 380),
        new("Support backlog", "tickets", 220, 18, 150, higherIsBetter: false),
        new("Uptime", "%", 99.86, 0.05, 99.8),
        new("Cost per order", "Rp k", 18.5, 0.6, 17, higherIsBetter: false),
        new("Conversion", "%", 3.1, 0.12, 3.0),
        new("Ops headcount", "FTE", 312, 1, 300, higherIsBetter: false),
        new("Security incidents", "/week", 3, 0.9, 2, higherIsBetter: false),
    ];

    /// <summary>A plant → area → asset hierarchy for the digital twin platform.</summary>
    public static List<TwinAsset> Assets()
    {
        var rng = new Random(5);
        var list = new List<TwinAsset>();
        string[] sites = ["Cikarang plant", "Surabaya plant", "Batam hub"];
        string[][] areas = [["Press line", "Paint shop", "Assembly"], ["Boiler house", "Packaging", "Cold store"], ["Inbound dock", "Sorting", "Outbound dock"]];
        string[] kinds = ["Robot", "Conveyor", "Compressor", "Chiller", "Press", "AGV"];
        for (var s = 0; s < sites.Length; s++)
        {
            for (var a = 0; a < 3; a++)
            {
                for (var i = 0; i < 4; i++)
                {
                    var kind = kinds[(s * 3 + a + i) % kinds.Length];
                    list.Add(new TwinAsset($"{sites[s][..3].ToUpperInvariant()}-{a + 1}{i + 1}", kind, sites[s], areas[s][a])
                    {
                        Health = 0.75 + rng.NextDouble() * 0.25,
                        Load = 0.4 + rng.NextDouble() * 0.5,
                        Temperature = 45 + rng.NextDouble() * 20,
                    });
                }
            }
        }

        return list;
    }

    public static readonly string[] AgentRoles = ["Triage", "Research", "Billing", "Fulfilment", "Fraud watch", "Report writer", "Scheduler", "Data quality"];
}

/// <summary>One asset in the digital twin platform.</summary>
public sealed class TwinAsset(string id, string kind, string site, string area)
{
    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string Site { get; } = site;
    public string Area { get; } = area;
    public double Health { get; set; }
    public double Load { get; set; }
    public double Temperature { get; set; }
    public bool Alarm => Health < 0.55 || Temperature > 80;

    public void Step(Random rng, double stress)
    {
        Load = Math.Clamp(Load + (rng.NextDouble() - 0.5) * 0.08 + stress * 0.01, 0.05, 1);
        Temperature = Math.Clamp(Temperature + (Load - 0.6) * 1.5 + (rng.NextDouble() - 0.5) * 1.2 + stress * 0.6, 20, 110);
        Health = Math.Clamp(Health - Math.Max(0, Temperature - 70) * 0.0015 - stress * 0.004 + 0.0008, 0.05, 1);
    }

    /// <summary>Remaining useful life in days from the current degradation trend (simple linear extrapolation).</summary>
    public double RulDays => Math.Max(0, (Health - 0.3) / Math.Max(1e-4, Math.Max(0, Temperature - 70) * 0.0015 + 0.0005) / 24);
}
