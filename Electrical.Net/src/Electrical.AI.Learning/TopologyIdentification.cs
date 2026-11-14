using System.Globalization;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.AI.Learning;

/// <summary>A switching configuration: the set of open switchable branches (identified by from/to bus).</summary>
/// <param name="Name">Short label, e.g. "base" or "optimal".</param>
public sealed record SwitchConfiguration(string Name, IReadOnlyList<(int From, int To)> OpenBranches)
{
    /// <summary>Copy of <paramref name="net"/> with <see cref="OpenBranches"/> out of service and every other branch in service.</summary>
    public PowerNetwork Apply(PowerNetwork net)
    {
        var c = net.Clone();
        foreach (var br in c.Branches) br.InService = true;
        foreach (var (f, t) in OpenBranches)
        {
            var br = c.Branches.FirstOrDefault(b => (b.From == f && b.To == t) || (b.From == t && b.To == f))
                ?? throw new ArgumentException($"No branch {f}-{t}.");
            br.InService = false;
        }
        return c;
    }

    public override string ToString() => $"{Name}: open {string.Join(", ", OpenBranches.Select(b => $"{b.From}-{b.To}"))}";
}

/// <summary>Reconfigurable test feeders.</summary>
public static class ReconfigurableFeeders
{
    /// <summary>
    /// Baran &amp; Wu 33-bus feeder with its five tie switches (8-21, 9-15, 12-22: 2 + j2 Ω; 18-33, 25-29: 0.5 + j0.5 Ω; Baran &amp; Wu 1989).
    /// The tie branches are added in service; use a <see cref="SwitchConfiguration"/> to open a radial set.
    /// </summary>
    public static PowerNetwork Case33WithTies()
    {
        var net = TestCases.Case33BW();
        double zb = 12.66 * 12.66 / net.BaseMva;
        foreach (var (f, t, r, x) in new[] { (8, 21, 2.0, 2.0), (9, 15, 2.0, 2.0), (12, 22, 2.0, 2.0), (18, 33, 0.5, 0.5), (25, 29, 0.5, 0.5) })
            net.Branches.Add(new Branch { From = f, To = t, R = r / zb, X = x / zb, Name = $"Tie {f}-{t}" });
        return net;
    }

    /// <summary>
    /// Radial configurations of <see cref="Case33WithTies"/>: the base case (all ties open), the minimum-loss configuration
    /// (open 7-8, 9-10, 14-15, 32-33, 25-29; ≈ 139.5 kW, Goswami &amp; Basu 1992) and six single tie/sectionalizer swaps.
    /// </summary>
    public static IReadOnlyList<SwitchConfiguration> Case33Configurations { get; } =
    [
        new("base", [(8, 21), (9, 15), (12, 22), (18, 33), (25, 29)]),
        new("optimal", [(7, 8), (9, 10), (14, 15), (32, 33), (25, 29)]),
        new("tie 8-21", [(7, 8), (9, 15), (12, 22), (18, 33), (25, 29)]),
        new("tie 9-15", [(8, 21), (13, 14), (12, 22), (18, 33), (25, 29)]),
        new("tie 12-22", [(8, 21), (9, 15), (20, 21), (18, 33), (25, 29)]),
        new("tie 18-33", [(8, 21), (9, 15), (12, 22), (32, 33), (25, 29)]),
        new("tie 25-29", [(8, 21), (9, 15), (12, 22), (18, 33), (28, 29)]),
        new("tie 18-33b", [(8, 21), (9, 15), (12, 22), (17, 18), (25, 29)]),
    ];
}

/// <summary>Configuration of <see cref="TopologyIdentification.GenerateSnapshots"/>.</summary>
public sealed record TopologySnapshotOptions
{
    public Func<PowerNetwork> Network { get; init; } = ReconfigurableFeeders.Case33WithTies;
    public IReadOnlyList<SwitchConfiguration> Configurations { get; init; } = ReconfigurableFeeders.Case33Configurations;
    /// <summary>Buses with voltage-magnitude measurements [pu].</summary>
    public IReadOnlyList<int> MeasuredBuses { get; init; } = [6, 12, 15, 18, 21, 22, 25, 29, 33];
    /// <summary>Also measure the substation P and Q [MW, Mvar].</summary>
    public bool MeasureSubstation { get; init; } = true;
    /// <summary>
    /// Append the load-level-invariant voltage-drop signature (1 − V)/‖1 − V‖ of the measured buses as extra classifier features
    /// (derived from the same measurements; it lets the tree classifier generalize across load levels).
    /// </summary>
    public bool SignatureFeatures { get; init; } = true;
    /// <summary>Snapshots per configuration.</summary>
    public int SnapshotsPerConfiguration { get; init; } = 60;
    /// <summary>System load multiplier range (uniform).</summary>
    public double MinLoadScale { get; init; } = 0.6;
    public double MaxLoadScale { get; init; } = 1.2;
    /// <summary>Relative standard deviation of independent per-load variation.</summary>
    public double LoadNoise { get; init; } = 0.1;
    /// <summary>Measurement noise: σ of voltage magnitude [pu] and relative σ of P/Q.</summary>
    public double VoltageNoisePu { get; init; } = 0.001;
    public double PowerNoise { get; init; } = 0.01;
    public int Seed { get; init; } = 5;
}

/// <summary>One labelled measurement snapshot.</summary>
public sealed record TopologySnapshot(double[] Features, string Configuration, double LoadScale);

/// <summary>Accuracy of topology identification on held-out snapshots.</summary>
public sealed record TopologyIdentificationReport(double ClassifierAccuracy, double CorrelationAccuracy, int TrainSamples, int TestSamples,
    int Configurations)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"classifier {ClassifierAccuracy:P1}, correlation {CorrelationAccuracy:P1} over {Configurations} configurations ({TrainSamples} train / {TestSamples} test)");
}

/// <summary>
/// Correlation-based topology identifier (non-ML baseline): every configuration is represented by its mean normalized voltage-drop
/// signature d = (1 − V)/‖1 − V‖ over the measured buses, which is nearly independent of the overall load level on a radial feeder
/// (drops scale with load); a snapshot is assigned to the configuration with the highest correlation (cosine similarity) of its own
/// signature with the configuration signature.
/// </summary>
public sealed class CorrelationTopologyIdentifier
{
    private readonly Dictionary<string, double[]> _sig = [];
    private readonly int _voltages;

    /// <param name="voltageCount">Number of leading features that are voltage magnitudes [pu].</param>
    public CorrelationTopologyIdentifier(IEnumerable<TopologySnapshot> training, int voltageCount)
    {
        _voltages = voltageCount;
        foreach (var g in training.GroupBy(s => s.Configuration))
        {
            var m = new double[voltageCount];
            int n = 0;
            foreach (var s in g)
            {
                var d = Signature(s.Features);
                for (int j = 0; j < m.Length; j++) m[j] += d[j];
                n++;
            }
            double norm = Math.Sqrt(m.Sum(v => v * v));
            for (int j = 0; j < m.Length; j++) m[j] /= norm > 0 ? norm : Math.Max(1, n);
            _sig[g.Key] = m;
        }
    }

    public IReadOnlyCollection<string> Configurations => _sig.Keys;

    /// <summary>Correlation of a snapshot with every configuration signature.</summary>
    public IReadOnlyDictionary<string, double> Correlations(ReadOnlySpan<double> x)
    {
        var d = Signature(x);
        var r = new Dictionary<string, double>();
        foreach (var (name, sig) in _sig)
        {
            double c = 0;
            for (int j = 0; j < sig.Length; j++) c += d[j] * sig[j];
            r[name] = c;
        }
        return r;
    }

    public string Identify(ReadOnlySpan<double> x) => Correlations(x).MaxBy(kv => kv.Value).Key;

    private double[] Signature(ReadOnlySpan<double> x)
    {
        var d = new double[_voltages];
        double s = 0;
        for (int j = 0; j < d.Length; j++) { d[j] = 1 - x[j]; s += d[j] * d[j]; }
        s = Math.Sqrt(s);
        if (s > 0) for (int j = 0; j < d.Length; j++) d[j] /= s;
        return d;
    }
}

/// <summary>
/// Topology (switching-state) identification from voltage / flow snapshots: simulates power flows of every radial configuration under
/// random load variation, then trains a classifier (GraviLearn random forest or ML.NET) and the correlation baseline
/// <see cref="CorrelationTopologyIdentifier"/>.
/// </summary>
public static class TopologyIdentification
{
    /// <summary>Feature names of the snapshots.</summary>
    public static string[] FeatureNames(TopologySnapshotOptions o)
    {
        var n = o.MeasuredBuses.Select(b => $"V@{b}").ToList();
        if (o.MeasureSubstation) n.AddRange(["Psub", "Qsub"]);
        if (o.SignatureFeatures) n.AddRange(o.MeasuredBuses.Select(b => $"dV@{b}"));
        return [.. n];
    }

    /// <summary>Generates snapshots (deterministic for a seed). Configurations that are not radial and connected throw.</summary>
    public static IReadOnlyList<TopologySnapshot> GenerateSnapshots(TopologySnapshotOptions? options = null)
    {
        var o = options ?? new TopologySnapshotOptions();
        var baseNet = o.Network();
        var rng = new Random(o.Seed);
        var result = new List<TopologySnapshot>();
        foreach (var cfg in o.Configurations)
        {
            var net = cfg.Apply(baseNet);
            if (!Topology.IsRadial(net) || Topology.Islands(net).Count != 1)
                throw new ArgumentException($"Configuration '{cfg.Name}' is not a connected radial network.");
            var baseLoads = net.Loads.Select(l => (l.P, l.Q)).ToArray();
            int slack = net.SlackBus?.Id ?? net.Buses[0].Id;
            for (int s = 0; s < o.SnapshotsPerConfiguration; s++)
            {
                double k = o.MinLoadScale + (o.MaxLoadScale - o.MinLoadScale) * rng.NextDouble();
                for (int i = 0; i < baseLoads.Length; i++)
                {
                    double f = k * Math.Max(0.1, 1 + o.LoadNoise * FaultDatasetGenerator.Gaussian(rng));
                    net.Loads[i].P = baseLoads[i].P * f;
                    net.Loads[i].Q = baseLoads[i].Q * f;
                }
                var pf = PowerFlowSolver.Solve(net);
                if (!pf.Converged) throw new InvalidOperationException($"Power flow did not converge for '{cfg.Name}'.");
                var x = new List<double>();
                foreach (var b in o.MeasuredBuses) x.Add(pf.Bus(b).Vm + o.VoltageNoisePu * FaultDatasetGenerator.Gaussian(rng));
                if (o.MeasureSubstation)
                {
                    var sb = pf.Bus(slack);
                    x.Add(sb.PGen * (1 + o.PowerNoise * FaultDatasetGenerator.Gaussian(rng)));
                    x.Add(sb.QGen * (1 + o.PowerNoise * FaultDatasetGenerator.Gaussian(rng)));
                }
                if (o.SignatureFeatures)
                {
                    var v = x.Take(o.MeasuredBuses.Count).ToArray();
                    double norm = Math.Sqrt(v.Sum(q => (1 - q) * (1 - q)));
                    x.AddRange(v.Select(q => norm > 0 ? (1 - q) / norm : 0));
                }
                result.Add(new TopologySnapshot([.. x], cfg.Name, k));
            }
        }
        return result;
    }

    /// <summary>Trains the classifier and the correlation baseline and reports held-out accuracy.</summary>
    public static (IClassificationModel Classifier, CorrelationTopologyIdentifier Correlation, TopologyIdentificationReport Report) Train(
        IReadOnlyList<TopologySnapshot> snapshots, TopologySnapshotOptions? options = null, ClassificationTrainingOptions? model = null,
        double testFraction = 0.3, int splitSeed = 3)
    {
        var o = options ?? new TopologySnapshotOptions();
        var idx = Enumerable.Range(0, snapshots.Count).ToArray();
        new Random(splitSeed).Shuffle(idx);
        int nTest = (int)Math.Round(snapshots.Count * testFraction);
        var test = idx.Take(nTest).Select(i => snapshots[i]).ToArray();
        var train = idx.Skip(nTest).Select(i => snapshots[i]).ToArray();
        var mo = (model ?? new ClassificationTrainingOptions()) with { FeatureNames = FeatureNames(o) };
        var clf = ModelTrainer.TrainClassifier(train.Select(s => s.Features).ToArray(), train.Select(s => s.Configuration).ToArray(), mo);
        var corr = new CorrelationTopologyIdentifier(train, o.MeasuredBuses.Count);
        var actual = test.Select(s => s.Configuration).ToArray();
        double accC = ModelTrainer.Accuracy(actual, clf.Predict(test.Select(s => s.Features).ToArray()));
        double accR = ModelTrainer.Accuracy(actual, test.Select(s => corr.Identify(s.Features)).ToArray());
        return (clf, corr, new TopologyIdentificationReport(accC, accR, train.Length, test.Length, o.Configurations.Count));
    }
}
