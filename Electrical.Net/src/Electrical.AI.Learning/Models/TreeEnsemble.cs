using System.Globalization;
using System.Text.Json;
using Gravicode.Science.GraviLearn.Trees;

namespace Electrical.AI.Learning;

/// <summary>How the trees of a <see cref="TreeEnsemble"/> are combined.</summary>
public enum TreeCombination
{
    /// <summary>Boosting: <c>Bias + Scale·Σ tree(x)</c>.</summary>
    Sum,
    /// <summary>Bagging / forest: mean of the trees' values (regression) or leaf class probabilities (classification).</summary>
    Average,
}

/// <summary>
/// Portable, dependency-free representation of a fitted tree ensemble (CART trees from GraviLearn flattened into arrays),
/// used for prediction and JSON persistence. A node goes left when <c>x[feature] ≤ threshold</c>, exactly as in GraviLearn's
/// <c>DecisionTree.Descend</c>, so a loaded ensemble predicts bit-identically to the fitted GraviLearn trees.
/// </summary>
public sealed class TreeEnsemble
{
    internal sealed record FlatTree(int[] Feature, double[] Threshold, int[] Left, int[] Right, double[] Value, double[][]? Probabilities);

    private readonly FlatTree[] _trees;

    internal TreeEnsemble(FlatTree[] trees, TreeCombination combination, int featureCount, double bias, double scale,
        IReadOnlyList<string>? classLabels, IReadOnlyList<string>? featureNames)
    {
        _trees = trees;
        Combination = combination;
        FeatureCount = featureCount;
        Bias = bias;
        Scale = scale;
        ClassLabels = classLabels;
        FeatureNames = featureNames;
    }

    /// <summary>Format tag written into the JSON file.</summary>
    public const string Format = "electrical-tree-ensemble/1";

    public TreeCombination Combination { get; }
    public int FeatureCount { get; }
    /// <summary>Initial prediction (boosting) — 0 for forests.</summary>
    public double Bias { get; }
    /// <summary>Per-tree shrinkage (boosting learning rate); 1 for forests.</summary>
    public double Scale { get; }
    /// <summary>Class labels in probability-column order (classification ensembles only).</summary>
    public IReadOnlyList<string>? ClassLabels { get; }
    public IReadOnlyList<string>? FeatureNames { get; }
    public int TreeCount => _trees.Length;
    /// <summary>Total number of nodes over all trees.</summary>
    public int NodeCount => _trees.Sum(t => t.Feature.Length);

    /// <summary>Flattens fitted GraviLearn trees.</summary>
    internal static TreeEnsemble FromGraviLearn(IEnumerable<DecisionTree> trees, TreeCombination combination, int featureCount, double bias, double scale,
        IReadOnlyList<string>? classLabels = null, IReadOnlyList<string>? featureNames = null) =>
        new(trees.Select(t => Flatten(t.Root ?? throw new InvalidOperationException("Tree is not fitted."))).ToArray(), combination, featureCount,
            bias, scale, classLabels, featureNames);

    internal static FlatTree Flatten(TreeNode root)
    {
        var nodes = new List<TreeNode>();
        var index = new Dictionary<TreeNode, int>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<TreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            index[n] = nodes.Count;
            nodes.Add(n);
            if (n.IsLeaf) continue;
            stack.Push(n.Right!);
            stack.Push(n.Left!);
        }
        int c = nodes.Count;
        var f = new int[c]; var th = new double[c]; var l = new int[c]; var r = new int[c]; var v = new double[c];
        double[][]? p = nodes.Any(n => n.ClassProbabilities is not null) ? new double[c][] : null;
        for (int i = 0; i < c; i++)
        {
            var n = nodes[i];
            v[i] = n.Value;
            if (p is not null) p[i] = n.ClassProbabilities is { } cp ? (double[])cp.Clone() : [];
            if (n.IsLeaf) { f[i] = -1; l[i] = r[i] = -1; continue; }
            f[i] = n.Feature; th[i] = n.Threshold; l[i] = index[n.Left!]; r[i] = index[n.Right!];
        }
        return new FlatTree(f, th, l, r, v, p);
    }

    private static int Leaf(FlatTree t, ReadOnlySpan<double> x)
    {
        int i = 0;
        while (t.Feature[i] >= 0) i = x[t.Feature[i]] <= t.Threshold[i] ? t.Left[i] : t.Right[i];
        return i;
    }

    /// <summary>Regression value (or boosted score).</summary>
    public double PredictValue(ReadOnlySpan<double> x)
    {
        if (x.Length != FeatureCount) throw new ArgumentException($"Expected {FeatureCount} features, got {x.Length}.", nameof(x));
        double s = 0;
        foreach (var t in _trees) s += t.Value[Leaf(t, x)];
        return Combination == TreeCombination.Sum ? Bias + Scale * s : s / Math.Max(1, _trees.Length);
    }

    /// <summary>Averaged leaf class probabilities (classification ensembles), aligned with <see cref="ClassLabels"/>.</summary>
    public double[] PredictProbabilities(ReadOnlySpan<double> x)
    {
        if (ClassLabels is null) throw new InvalidOperationException("Not a classification ensemble.");
        if (x.Length != FeatureCount) throw new ArgumentException($"Expected {FeatureCount} features, got {x.Length}.", nameof(x));
        var p = new double[ClassLabels.Count];
        foreach (var t in _trees)
        {
            var leaf = t.Probabilities![Leaf(t, x)];
            for (int c = 0; c < Math.Min(p.Length, leaf.Length); c++) p[c] += leaf[c];
        }
        for (int c = 0; c < p.Length; c++) p[c] /= Math.Max(1, _trees.Length);
        return p;
    }

    /// <summary>Writes the ensemble as JSON (<see cref="Format"/>).</summary>
    public void Save(Stream stream)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        w.WriteStartObject();
        w.WriteString("format", Format);
        w.WriteString("combination", Combination.ToString());
        w.WriteNumber("featureCount", FeatureCount);
        w.WriteNumber("bias", Bias);
        w.WriteNumber("scale", Scale);
        if (ClassLabels is not null) { w.WriteStartArray("classLabels"); foreach (var c in ClassLabels) w.WriteStringValue(c); w.WriteEndArray(); }
        if (FeatureNames is not null) { w.WriteStartArray("featureNames"); foreach (var c in FeatureNames) w.WriteStringValue(c); w.WriteEndArray(); }
        w.WriteStartArray("trees");
        foreach (var t in _trees)
        {
            w.WriteStartObject();
            WriteInts(w, "feature", t.Feature);
            WriteDoubles(w, "threshold", t.Threshold);
            WriteInts(w, "left", t.Left);
            WriteInts(w, "right", t.Right);
            WriteDoubles(w, "value", t.Value);
            if (t.Probabilities is not null)
            {
                w.WriteStartArray("probabilities");
                foreach (var p in t.Probabilities) { w.WriteStartArray(); foreach (var d in p) w.WriteNumberValue(d); w.WriteEndArray(); }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        Save(fs);
    }

    /// <summary>Reads an ensemble written by <see cref="Save(Stream)"/>.</summary>
    public static TreeEnsemble Load(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.GetProperty("format").GetString() != Format) throw new InvalidDataException("Not an Electrical.Net tree-ensemble file.");
        var comb = Enum.Parse<TreeCombination>(root.GetProperty("combination").GetString()!);
        int fc = root.GetProperty("featureCount").GetInt32();
        double bias = root.GetProperty("bias").GetDouble(), scale = root.GetProperty("scale").GetDouble();
        string[]? labels = root.TryGetProperty("classLabels", out var cl) ? cl.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;
        string[]? names = root.TryGetProperty("featureNames", out var fn) ? fn.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;
        var trees = root.GetProperty("trees").EnumerateArray().Select(t => new FlatTree(
            Ints(t.GetProperty("feature")), Doubles(t.GetProperty("threshold")), Ints(t.GetProperty("left")), Ints(t.GetProperty("right")),
            Doubles(t.GetProperty("value")),
            t.TryGetProperty("probabilities", out var p) ? p.EnumerateArray().Select(Doubles).ToArray() : null)).ToArray();
        return new TreeEnsemble(trees, comb, fc, bias, scale, labels, names);
    }

    public static TreeEnsemble Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs);
    }

    private static void WriteInts(Utf8JsonWriter w, string name, int[] v)
    {
        w.WriteStartArray(name);
        foreach (var x in v) w.WriteNumberValue(x);
        w.WriteEndArray();
    }

    private static void WriteDoubles(Utf8JsonWriter w, string name, double[] v)
    {
        w.WriteStartArray(name);
        foreach (var x in v) w.WriteNumberValue(x); // round-trip "R" formatting by Utf8JsonWriter
        w.WriteEndArray();
    }

    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();
    private static double[] Doubles(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Combination} ensemble of {TreeCount} trees ({NodeCount} nodes, {FeatureCount} features)");
}
