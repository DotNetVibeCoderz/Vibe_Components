namespace GnuRadio.Extensions;

/// <summary>Higher-order statistics of a block of complex symbols, the classic features for modulation recognition.</summary>
public readonly record struct CumulantFeatures(double C20, double C21, double C40, double C41, double C42, double C63, double AmplitudeVariance)
{
    /// <summary>The feature vector used by <see cref="ModulationClassifier"/>.</summary>
    public double[] ToVector() => [Math.Abs(C40), Math.Abs(C42), Math.Abs(C63) / 10, AmplitudeVariance];

    /// <summary>Normalised cumulants of <paramref name="x"/> (power normalised to 1, mean removed).</summary>
    public static CumulantFeatures Compute(ReadOnlySpan<Complex32> x)
    {
        var n = x.Length;
        if (n < 16) throw new ArgumentException("Need at least 16 samples.", nameof(x));
        System.Numerics.Complex mean = 0;
        foreach (var s in x) mean += s;
        mean /= n;
        double power = 0;
        foreach (var s in x) power += ((System.Numerics.Complex)s - mean).Magnitude * ((System.Numerics.Complex)s - mean).Magnitude;
        power /= n;
        var scale = 1 / Math.Sqrt(power);

        System.Numerics.Complex m20 = 0, m40 = 0, m41 = 0;
        double m21 = 0, m42 = 0, m63 = 0, amp = 0, amp2 = 0;
        foreach (var raw in x)
        {
            var s = ((System.Numerics.Complex)raw - mean) * scale;
            var s2 = s * s;
            var a2 = s.Magnitude * s.Magnitude;
            m20 += s2;
            m21 += a2;
            m40 += s2 * s2;
            m41 += s2 * a2;
            m42 += a2 * a2;
            m63 += a2 * a2 * a2;
            amp += Math.Sqrt(a2);
            amp2 += a2;
        }
        m20 /= n; m21 /= n; m40 /= n; m41 /= n; m42 /= n; m63 /= n;
        var c40 = m40 - 3 * m20 * m20;
        var c41 = m41 - 3 * m20 * m21;
        var c42 = m42 - (m20 * System.Numerics.Complex.Conjugate(m20)).Real - 2 * m21 * m21;
        var c63 = m63 - 9 * m42 * m21 + 12 * Math.Pow(m21, 3);
        var meanAmp = amp / n;
        var ampVar = amp2 / n - meanAmp * meanAmp;
        return new CumulantFeatures(m20.Magnitude, m21, c40.Magnitude * Math.Sign(c40.Real), c41.Magnitude, c42, c63, ampVar);
    }
}

/// <summary>
/// Nearest-centroid modulation classifier on cumulant features ("machine learning for RF" without a heavy ML
/// stack). Ships with centroids for BPSK, QPSK, 8PSK, 16QAM and 64QAM learned from synthetic data, and can be
/// retrained on your own labelled captures with <see cref="Train"/>.
/// </summary>
public sealed class ModulationClassifier
{
    private readonly Dictionary<string, double[]> _centroids = new();

    public IReadOnlyDictionary<string, double[]> Centroids => _centroids;

    /// <summary>Trains on synthetic, noise-free symbols of the standard constellations.</summary>
    public static ModulationClassifier CreateDefault(int samplesPerClass = 8, int blockSize = 4096, int seed = 3)
    {
        var classifier = new ModulationClassifier();
        var rng = new FastRandom(seed);
        foreach (var name in new[] { "bpsk", "qpsk", "8psk", "16qam", "64qam" })
        {
            var c = Digital.Constellation.FromName(name);
            var examples = new List<Complex32[]>();
            for (var e = 0; e < samplesPerClass; e++)
            {
                var block = new Complex32[blockSize];
                for (var i = 0; i < blockSize; i++) block[i] = c.Points[rng.Next(c.Arity)] + rng.NextComplexGaussian() * 0.05f;
                examples.Add(block);
            }
            classifier.Train(c.Name, examples);
        }
        return classifier;
    }

    /// <summary>Adds or replaces the centroid of class <paramref name="label"/> from example blocks.</summary>
    public void Train(string label, IEnumerable<Complex32[]> examples)
    {
        double[]? sum = null;
        var count = 0;
        foreach (var block in examples)
        {
            var v = CumulantFeatures.Compute(block).ToVector();
            sum ??= new double[v.Length];
            for (var i = 0; i < v.Length; i++) sum[i] += v[i];
            count++;
        }
        if (sum is null) throw new ArgumentException("No examples.", nameof(examples));
        for (var i = 0; i < sum.Length; i++) sum[i] /= count;
        _centroids[label] = sum;
    }

    /// <summary>Classifies a block of symbol-rate samples; returns the label and a softmax-style confidence per class.</summary>
    public (string Label, IReadOnlyDictionary<string, double> Scores) Classify(ReadOnlySpan<Complex32> symbols)
    {
        if (_centroids.Count == 0) throw new InvalidOperationException("The classifier has not been trained.");
        var v = CumulantFeatures.Compute(symbols).ToVector();
        var distances = _centroids.ToDictionary(kv => kv.Key, kv => Math.Sqrt(kv.Value.Zip(v, (a, b) => (a - b) * (a - b)).Sum()));
        var weights = distances.ToDictionary(kv => kv.Key, kv => Math.Exp(-kv.Value * 20));
        var total = weights.Values.Sum();
        var scores = weights.ToDictionary(kv => kv.Key, kv => total > 0 ? kv.Value / total : 0);
        var best = distances.MinBy(kv => kv.Value).Key;
        return (best, scores);
    }
}
