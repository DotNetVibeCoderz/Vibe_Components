using System.Globalization;
using System.Numerics;
using Electrical.Core.Network;
using Electrical.ShortCircuit;

namespace Electrical.AI.Learning;

/// <summary>Configuration of <see cref="FaultDatasetGenerator.Generate"/>.</summary>
public sealed record FaultDatasetOptions
{
    /// <summary>Network factory (a fresh copy is used).</summary>
    public Func<PowerNetwork> Network { get; init; } = TestCases.Case33BW;
    /// <summary>Network infeeds; empty = generators with subtransient data are the sources.</summary>
    public IReadOnlyList<ExternalGrid> ExternalGrids { get; init; } = [new ExternalGrid { Bus = 1, SkMva = 250, RxRatio = 0.1, Z0OverZ1 = 1 }];
    /// <summary>Buses with voltage measurements (substation / PMU buses): phase voltage magnitudes [pu].</summary>
    public IReadOnlyList<int> MeasuredBuses { get; init; } = [1, 6, 13, 18, 22, 25, 33];
    /// <summary>Branch indices with current measurements at the from end: phase currents and residual [kA].</summary>
    public IReadOnlyList<int> MeasuredBranches { get; init; } = [0, 1, 17, 21, 24];
    /// <summary>Number of fault scenarios.</summary>
    public int Samples { get; init; } = 1200;
    /// <summary>Probability that a scenario is a fault along a line (otherwise at a bus).</summary>
    public double LineFaultProbability { get; init; } = 0.5;
    /// <summary>Positions along a line (fraction of its length from the from bus) at which line faults are placed.</summary>
    public IReadOnlyList<double> LinePositions { get; init; } = [0.25, 0.5, 0.75];
    /// <summary>Fault resistance is uniform in [0, MaxFaultResistanceOhm] [Ω].</summary>
    public double MaxFaultResistanceOhm { get; init; } = 5;
    /// <summary>Relative standard deviation of multiplicative measurement noise.</summary>
    public double MeasurementNoise { get; init; } = 0.005;
    /// <summary>Rotate the faulted phases at random (A/B/C) so every phase combination appears.</summary>
    public bool RandomizePhases { get; init; } = true;
    public int Seed { get; init; } = 7;

    /// <summary>Preset for the IEEE 14-bus system (generator sources, PMUs at the generator buses and bus 9).</summary>
    public static FaultDatasetOptions Case14() => new()
    {
        Network = TestCases.Case14,
        ExternalGrids = [],
        MeasuredBuses = [1, 2, 3, 6, 8, 9],
        MeasuredBranches = [0, 1, 2, 6, 7],
        MaxFaultResistanceOhm = 10,
    };
}

/// <summary>One simulated fault.</summary>
/// <param name="Features">Measurement vector (see <see cref="FaultDataset.FeatureNames"/>).</param>
/// <param name="Code">Fault code: AG, BG, CG, AB, BC, AC, ABG, BCG, ACG or ABC.</param>
/// <param name="NearestBusId">Bus nearest to the fault (the faulted bus, or the closer end of the faulted line).</param>
/// <param name="BranchIndex">Faulted line (null for bus faults).</param>
/// <param name="Position">Fraction along the line from its from bus (0 for bus faults).</param>
/// <param name="FaultResistanceOhm">Fault resistance [Ω].</param>
public sealed record FaultSample(double[] Features, string Code, FaultType Type, int NearestBusId, int? BranchIndex, double Position,
    double FaultResistanceOhm);

/// <summary>A deterministic fault-simulation dataset.</summary>
public sealed class FaultDataset(IReadOnlyList<FaultSample> samples, string[] featureNames, PowerNetwork network)
{
    public IReadOnlyList<FaultSample> Samples { get; } = samples;
    public string[] FeatureNames { get; } = featureNames;
    /// <summary>The unmodified network (for topology queries such as hop distance).</summary>
    public PowerNetwork Network { get; } = network;

    /// <summary>Deterministic shuffled split into train and held-out test samples.</summary>
    public (IReadOnlyList<FaultSample> Train, IReadOnlyList<FaultSample> Test) Split(double testFraction = 0.25, int seed = 11)
    {
        var idx = Enumerable.Range(0, Samples.Count).ToArray();
        new Random(seed).Shuffle(idx);
        int nTest = (int)Math.Round(Samples.Count * testFraction);
        return (idx.Skip(nTest).Select(i => Samples[i]).ToArray(), idx.Take(nTest).Select(i => Samples[i]).ToArray());
    }
}

/// <summary>
/// Generates labelled fault scenarios by IEC 60909 sequence-network short-circuit simulation (<see cref="ShortCircuitStudy"/>):
/// random fault type, location (a bus, or a point along a line obtained by splitting the line at a new bus) and fault resistance;
/// the features are what substation / PMU instruments would record (phase voltage magnitudes at the measured buses, phase and residual
/// current magnitudes on the measured branches) with multiplicative Gaussian noise. Phases are rotated at random so that all ten phase
/// codes occur (the network is symmetrical, so rotating magnitudes is exact).
/// </summary>
public static class FaultDatasetGenerator
{
    /// <summary>All fault codes in canonical order.</summary>
    public static IReadOnlyList<string> Codes { get; } = ["AG", "BG", "CG", "AB", "BC", "AC", "ABG", "BCG", "ACG", "ABC"];

    public static FaultDataset Generate(FaultDatasetOptions? options = null)
    {
        var o = options ?? new FaultDatasetOptions();
        var baseNet = o.Network();
        var sc = new ShortCircuitOptions { ExternalGrids = o.ExternalGrids };
        var baseStudy = new ShortCircuitStudy(baseNet, sc);
        var lines = Enumerable.Range(0, baseNet.Branches.Count).Where(k => baseNet.Branches[k].InService && !baseNet.Branches[k].IsTransformer).ToArray();
        var splitStudies = new Dictionary<(int, int), (ShortCircuitStudy Study, int MidBus)>();
        var rng = new Random(o.Seed);
        var names = FeatureNames(o);
        var samples = new List<FaultSample>(o.Samples);
        FaultType[] types = [FaultType.SingleLineToGround, FaultType.LineToLine, FaultType.DoubleLineToGround, FaultType.ThreePhase];

        for (int s = 0; s < o.Samples; s++)
        {
            var type = types[rng.Next(types.Length)];
            double rf = rng.NextDouble() * o.MaxFaultResistanceOhm;
            int rot = o.RandomizePhases ? rng.Next(3) : 0;
            FaultResult res;
            int nearest; int? branch = null; double pos = 0;
            if (lines.Length > 0 && o.LinePositions.Count > 0 && rng.NextDouble() < o.LineFaultProbability)
            {
                int k = lines[rng.Next(lines.Length)];
                int pi = rng.Next(o.LinePositions.Count);
                pos = o.LinePositions[pi];
                if (!splitStudies.TryGetValue((k, pi), out var st))
                {
                    var (net, mid) = SplitLine(baseNet, k, pos);
                    st = (new ShortCircuitStudy(net, sc), mid);
                    splitStudies[(k, pi)] = st;
                }
                res = st.Study.Calculate(st.MidBus, type, new Complex(rf, 0));
                var br = baseNet.Branches[k];
                nearest = pos <= 0.5 ? br.From : br.To;
                branch = k;
            }
            else
            {
                nearest = baseNet.Buses[rng.Next(baseNet.BusCount)].Id;
                res = baseStudy.Calculate(nearest, type, new Complex(rf, 0));
            }
            var x = Features(res, o, rot, rng);
            samples.Add(new FaultSample(x, Code(type, rot), type, nearest, branch, pos, rf));
        }
        return new FaultDataset(samples, names, baseNet);
    }

    /// <summary>Feature names: <c>V{a,b,c}@bus</c> then <c>I{a,b,c,0}@branch</c>.</summary>
    public static string[] FeatureNames(FaultDatasetOptions o)
    {
        var n = new List<string>();
        foreach (var b in o.MeasuredBuses) n.AddRange([$"Va@{b}", $"Vb@{b}", $"Vc@{b}"]);
        foreach (var k in o.MeasuredBranches) n.AddRange([$"Ia@br{k}", $"Ib@br{k}", $"Ic@br{k}", $"I0@br{k}"]);
        return [.. n];
    }

    /// <summary>Phase code of a fault simulated on the reference phases (a for SLG, b–c otherwise) after rotating phases by <paramref name="rotation"/>.</summary>
    public static string Code(FaultType type, int rotation)
    {
        int[] ph = type switch
        {
            FaultType.SingleLineToGround => [0],
            FaultType.ThreePhase => [0, 1, 2],
            _ => [1, 2],
        };
        var letters = ph.Select(p => (p + rotation) % 3).Order().Select(p => "ABC"[p]);
        string g = type is FaultType.SingleLineToGround or FaultType.DoubleLineToGround ? "G" : "";
        return string.Concat(letters) + g;
    }

    /// <summary>Fault type of a phase code.</summary>
    public static FaultType TypeOf(string code) => code switch
    {
        "ABC" => FaultType.ThreePhase,
        _ when code.Length == 2 && code[1] == 'G' => FaultType.SingleLineToGround,
        _ when code.EndsWith('G') => FaultType.DoubleLineToGround,
        _ => FaultType.LineToLine,
    };

    private static double[] Features(FaultResult r, FaultDatasetOptions o, int rot, Random rng)
    {
        var x = new double[o.MeasuredBuses.Count * 3 + o.MeasuredBranches.Count * 4];
        int j = 0;
        double Noise() => 1 + o.MeasurementNoise * Gaussian(rng);
        foreach (var b in o.MeasuredBuses)
        {
            var v = r.Voltages.First(q => q.BusId == b);
            Rotated(x, j, [v.Va, v.Vb, v.Vc], rot);
            for (int q = 0; q < 3; q++) x[j + q] *= Noise();
            j += 3;
        }
        foreach (var k in o.MeasuredBranches)
        {
            var c = r.Branches.FirstOrDefault(q => q.Index == k);
            double[] i = c is null ? [0, 0, 0] : [c.IaKa, c.IbKa, c.IcKa];
            Rotated(x, j, i, rot);
            x[j + 3] = (c?.ResidualKa ?? 0);
            for (int q = 0; q < 4; q++) x[j + q] *= Noise();
            j += 4;
        }
        return x;
    }

    private static void Rotated(double[] dst, int offset, double[] abc, int rot)
    {
        for (int p = 0; p < 3; p++) dst[offset + (p + rot) % 3] = abc[p];
    }

    /// <summary>Copy of <paramref name="net"/> with line <paramref name="k"/> split at <paramref name="fraction"/> by a new bus (returned id).</summary>
    public static (PowerNetwork Network, int MidBusId) SplitLine(PowerNetwork net, int k, double fraction)
    {
        var c = net.Clone();
        var br = c.Branches[k];
        var from = c.GetBus(br.From);
        int mid = c.Buses.Max(b => b.Id) + 1;
        c.Buses.Add(new Bus { Id = mid, BaseKv = from.BaseKv, Name = $"F{br.From}-{br.To}@{fraction.ToString("0.##", CultureInfo.InvariantCulture)}", Area = from.Area, Zone = from.Zone });
        c.InvalidateIndex();
        double a = fraction, b = 1 - fraction;
        var second = new Branch
        {
            From = mid, To = br.To, Name = br.Name, Kind = br.Kind, R = br.R * b, X = br.X * b, B = br.B * b, RateA = br.RateA,
            R0 = br.R0 * b, X0 = br.X0 * b, B0 = br.B0 * b, LengthKm = br.LengthKm * b,
        };
        br.To = mid; br.R *= a; br.X *= a; br.B *= a; br.R0 *= a; br.X0 *= a; br.B0 *= a; br.LengthKm *= a;
        c.Branches.Add(second);
        return (c, mid);
    }

    internal static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

/// <summary>Accuracy summary of <see cref="FaultLearning.Train"/>.</summary>
/// <param name="CodeAccuracy">Exact phase-code accuracy (10 classes) on the held-out set.</param>
/// <param name="TypeAccuracy">Fault-type accuracy (3φ / SLG / LL / DLG).</param>
/// <param name="LocationAccuracy">Exact nearest-bus accuracy.</param>
/// <param name="LocationWithinOneHop">Predicted bus equal to or adjacent to the true nearest bus.</param>
public sealed record FaultLearningReport(double CodeAccuracy, double TypeAccuracy, double LocationAccuracy, double LocationWithinOneHop,
    int TrainSamples, int TestSamples)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"code {CodeAccuracy:P1}, type {TypeAccuracy:P1}, location {LocationAccuracy:P1} (±1 bus {LocationWithinOneHop:P1}), train {TrainSamples}, test {TestSamples}");
}

/// <summary>Trained fault classifier (phase code) and locator (nearest bus).</summary>
public sealed class FaultLearningModel(IClassificationModel classifier, IClassificationModel locator)
{
    public IClassificationModel Classifier { get; } = classifier;
    public IClassificationModel Locator { get; } = locator;

    /// <summary>Predicts (phase code, nearest bus id) from a measurement vector.</summary>
    public (string Code, FaultType Type, int BusId) Predict(ReadOnlySpan<double> features)
    {
        var code = Classifier.Predict(features);
        return (code, FaultDatasetGenerator.TypeOf(code), int.Parse(Locator.Predict(features), CultureInfo.InvariantCulture));
    }

    /// <summary>Saves both models into a directory (<c>classifier.*</c>, <c>locator.*</c>).</summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        Classifier.Save(Path.Combine(directory, "classifier" + Ext(Classifier)));
        Locator.Save(Path.Combine(directory, "locator" + Ext(Locator)));
    }

    public static FaultLearningModel Load(string directory) => new(
        ModelTrainer.LoadClassifier(Find(directory, "classifier")), ModelTrainer.LoadClassifier(Find(directory, "locator")));

    internal static string Ext(IClassificationModel m) => m is MlNetClassifier ? ".zip" : ".json";

    internal static string Find(string dir, string stem) =>
        new[] { ".json", ".zip" }.Select(e => Path.Combine(dir, stem + e)).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"No {stem}.json/.zip in {dir}.");
}

/// <summary>Trains and evaluates fault classification and location models on a <see cref="FaultDataset"/>.</summary>
public static class FaultLearning
{
    public static (FaultLearningModel Model, FaultLearningReport Report) Train(FaultDataset dataset, ClassificationTrainingOptions? options = null,
        double testFraction = 0.25, int splitSeed = 11)
    {
        var o = (options ?? new ClassificationTrainingOptions()) with { FeatureNames = dataset.FeatureNames };
        var (train, test) = dataset.Split(testFraction, splitSeed);
        var x = train.Select(s => s.Features).ToArray();
        var cls = ModelTrainer.TrainClassifier(x, train.Select(s => s.Code).ToArray(), o);
        var loc = ModelTrainer.TrainClassifier(x, train.Select(s => s.NearestBusId.ToString(CultureInfo.InvariantCulture)).ToArray(), o);
        var model = new FaultLearningModel(cls, loc);
        return (model, Evaluate(model, dataset.Network, test, train.Count));
    }

    /// <summary>Scores a model on samples.</summary>
    public static FaultLearningReport Evaluate(FaultLearningModel model, PowerNetwork network, IReadOnlyList<FaultSample> test, int trainCount = 0)
    {
        var xt = test.Select(s => s.Features).ToArray();
        var codes = model.Classifier.Predict(xt);
        var buses = model.Locator.Predict(xt).Select(b => int.Parse(b, CultureInfo.InvariantCulture)).ToArray();
        int code = 0, type = 0, loc = 0, hop = 0;
        var hops = new Dictionary<int, int[]>();
        for (int i = 0; i < test.Count; i++)
        {
            if (codes[i] == test[i].Code) code++;
            if (FaultDatasetGenerator.TypeOf(codes[i]) == test[i].Type) type++;
            if (buses[i] == test[i].NearestBusId) loc++;
            if (!hops.TryGetValue(test[i].NearestBusId, out var d)) hops[test[i].NearestBusId] = d = Topology.HopDistance(network, test[i].NearestBusId);
            if (d[network.IndexOf(buses[i])] is >= 0 and <= 1) hop++;
        }
        double n = Math.Max(1, test.Count);
        return new FaultLearningReport(code / n, type / n, loc / n, hop / n, trainCount, test.Count);
    }
}
