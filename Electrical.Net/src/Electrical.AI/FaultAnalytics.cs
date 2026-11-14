using System.Numerics;
using Electrical.Core.Units;
using Electrical.ShortCircuit;

namespace Electrical.AI;

/// <summary>Result of fault classification.</summary>
/// <param name="Type">Fault type (null = no fault / load condition).</param>
/// <param name="Phases">Faulted phases, e.g. "A", "BC", "ABC" (empty for no fault).</param>
/// <param name="Grounded">True when the zero-sequence current indicates an earth path.</param>
/// <param name="NegativeToPositive">|I₂|/|I₁|.</param>
/// <param name="ZeroToPositive">|I₀|/|I₁|.</param>
public sealed record FaultClassification(FaultType? Type, string Phases, bool Grounded, SequenceSet SequenceCurrents, double NegativeToPositive,
    double ZeroToPositive, string Description, string DescriptionId)
{
    public bool IsFault => Type is not null;

    /// <summary>Short code: "AG", "BC", "BCG", "ABC" or "none".</summary>
    public string Code => Type is null ? "none" : Phases + (Grounded && Type != FaultType.ThreePhase ? "G" : "");
}

/// <summary>
/// Rule-based fault classifier on three-phase current phasors using symmetrical components (e.g. Blackburn, "Protective Relaying", ch. 4):
/// balanced currents (|I₂|, |I₀| ≪ |I₁|) → three-phase; significant |I₀| → earth fault (one high phase = SLG, two = DLG);
/// significant |I₂| without |I₀| → line-to-line. Faulted phases are those whose magnitude exceeds a fraction of the largest phase current.
/// </summary>
public static class FaultClassifier
{
    /// <param name="currents">Phase currents (any consistent unit, e.g. kA).</param>
    /// <param name="pickup">Overcurrent pick-up in the same unit; below it the condition is classified as no fault (0 = always classify).</param>
    /// <param name="sequenceThreshold">Ratio |I₂|/|I₁| or |I₀|/|I₁| above which the component is considered significant (default 0.1).</param>
    /// <param name="phaseFraction">A phase is faulted when |I_p| ≥ fraction·max|I| (default 0.5).</param>
    public static FaultClassification Classify(ThreePhase currents, double pickup = 0, double sequenceThreshold = 0.1, double phaseFraction = 0.5)
    {
        var seq = SymmetricalComponents.FromPhases(currents);
        double i1 = seq.Positive.Magnitude, i2 = seq.Negative.Magnitude, i0 = seq.Zero.Magnitude;
        double r2 = i1 > 1e-12 ? i2 / i1 : double.PositiveInfinity, r0 = i1 > 1e-12 ? i0 / i1 : double.PositiveInfinity;
        double[] mag = [currents.A.Magnitude, currents.B.Magnitude, currents.C.Magnitude];
        double max = mag.Max();
        if (max <= 1e-12 || max < pickup)
            return new FaultClassification(null, "", false, seq, r2, r0, "No fault: currents below pick-up.", "Tidak ada gangguan: arus di bawah setelan pick-up.");
        bool grounded = r0 > sequenceThreshold;
        var order = Enumerable.Range(0, 3).OrderByDescending(p => mag[p]).ToArray();
        var faulted = order.Where(p => mag[p] >= phaseFraction * max).OrderBy(p => p).ToArray();
        FaultType type;
        int[] phases;
        if (r2 < sequenceThreshold && r0 < sequenceThreshold) { type = FaultType.ThreePhase; phases = [0, 1, 2]; }
        else if (grounded)
        {
            if (faulted.Length == 1) { type = FaultType.SingleLineToGround; phases = faulted; }
            else { type = FaultType.DoubleLineToGround; phases = order.Take(2).OrderBy(p => p).ToArray(); }
        }
        else { type = FaultType.LineToLine; phases = order.Take(2).OrderBy(p => p).ToArray(); }
        string ph = string.Concat(phases.Select(p => "ABC"[p]));
        string g = grounded && type != FaultType.ThreePhase ? "G" : "";
        var (en, id) = type switch
        {
            FaultType.ThreePhase => ("Three-phase (balanced) fault", "Gangguan tiga fasa (simetris)"),
            FaultType.SingleLineToGround => ($"Single-line-to-ground fault on phase {ph}", $"Gangguan satu fasa ke tanah pada fasa {ph}"),
            FaultType.LineToLine => ($"Line-to-line fault between phases {ph[0]} and {ph[1]}", $"Gangguan antar fasa {ph[0]}-{ph[1]}"),
            _ => ($"Double-line-to-ground fault on phases {ph[0]} and {ph[1]}", $"Gangguan dua fasa ke tanah pada fasa {ph[0]}-{ph[1]}"),
        };
        string stats = string.Create(System.Globalization.CultureInfo.InvariantCulture, $" (|I2|/|I1| = {r2:0.00}, |I0|/|I1| = {r0:0.00}, code {ph}{g})");
        return new FaultClassification(type, ph, grounded, seq, r2, r0, en + stats, id + stats);
    }
}

/// <summary>Fault location estimate.</summary>
/// <param name="DistanceKm">Estimated distance from the relay [km].</param>
/// <param name="ApparentImpedanceOhm">Loop impedance seen by the relay [Ω].</param>
/// <param name="Loop">Measuring loop used, e.g. "A-G" or "B-C".</param>
public sealed record FaultLocation(double DistanceKm, Complex ApparentImpedanceOhm, string Loop)
{
    /// <summary>Distance as a fraction of a line length [km].</summary>
    public double FractionOf(double lineLengthKm) => DistanceKm / lineLengthKm;
}

/// <summary>
/// One-ended simple reactance method (e.g. IEEE C37.114 §6): apparent loop impedance Z = V_loop/I_loop with zero-sequence compensation
/// k₀ = (Z₀ − Z₁)/(3Z₁) for earth loops; distance = Im(Z)/Im(z₁ per km). Fault resistance is ignored except through its reactive
/// component (accurate for radial feeds where the fault current is in phase with the relay current).
/// </summary>
public static class FaultLocator
{
    /// <param name="voltagesKv">Phase-to-ground voltages at the relay [kV].</param>
    /// <param name="currentsKa">Phase currents at the relay, flowing into the line [kA].</param>
    /// <param name="z1OhmPerKm">Positive-sequence line impedance [Ω/km].</param>
    /// <param name="z0OhmPerKm">Zero-sequence line impedance [Ω/km].</param>
    public static FaultLocation Locate(ThreePhase voltagesKv, ThreePhase currentsKa, FaultClassification fault, Complex z1OhmPerKm, Complex z0OhmPerKm)
    {
        if (fault.Type is null) throw new ArgumentException("No fault to locate.", nameof(fault));
        Complex z;
        string loop;
        int p = "ABC".IndexOf(fault.Phases[0]);
        switch (fault.Type)
        {
            case FaultType.SingleLineToGround:
            {
                var k0 = (z0OhmPerKm - z1OhmPerKm) / (3 * z1OhmPerKm);
                var i3i0 = currentsKa.A + currentsKa.B + currentsKa.C;
                z = voltagesKv[p] / (currentsKa[p] + k0 * i3i0);
                loop = $"{fault.Phases[0]}-G";
                break;
            }
            case FaultType.LineToLine or FaultType.DoubleLineToGround:
            {
                int q = "ABC".IndexOf(fault.Phases[1]);
                z = (voltagesKv[p] - voltagesKv[q]) / (currentsKa[p] - currentsKa[q]);
                loop = $"{fault.Phases[0]}-{fault.Phases[1]}";
                break;
            }
            default:
                z = voltagesKv.A / currentsKa.A;
                loop = "A-B-C";
                break;
        }
        return new FaultLocation(z.Imaginary / z1OhmPerKm.Imaginary, z, loop);
    }
}

/// <summary>Result of k-means clustering.</summary>
/// <param name="Assignments">Cluster of every profile.</param>
/// <param name="Centroids">Cluster centres (in the normalized space when normalization was used).</param>
/// <param name="Inertia">Σ squared distances to the assigned centroid.</param>
public sealed record ClusteringResult(int[] Assignments, double[][] Centroids, double Inertia, int Iterations, bool Normalized)
{
    public int K => Centroids.Length;
    public int[] Sizes => Enumerable.Range(0, K).Select(c => Assignments.Count(a => a == c)).ToArray();

    /// <summary>Nearest centroid of a new profile (normalized the same way).</summary>
    public int Predict(ReadOnlySpan<double> profile)
    {
        var x = Normalized ? LoadProfileClustering.NormalizeShape(profile) : profile.ToArray();
        int best = 0; double bd = double.PositiveInfinity;
        for (int c = 0; c < K; c++) { double d = LoadProfileClustering.SquaredDistance(x, Centroids[c]); if (d < bd) { bd = d; best = c; } }
        return best;
    }
}

/// <summary>
/// Load-profile clustering (customer segmentation / typical-day extraction): k-means with k-means++ seeding (Arthur &amp; Vassilvitskii 2007),
/// deterministic seed, several restarts keeping the lowest inertia. Profiles are optionally normalized by their peak so the clustering
/// groups shapes rather than sizes.
/// </summary>
public static class LoadProfileClustering
{
    public static ClusteringResult KMeans(IReadOnlyList<double[]> profiles, int k, bool normalize = true, int seed = 42, int restarts = 5, int maxIterations = 100)
    {
        if (profiles.Count < k || k < 1) throw new ArgumentException("Need at least k profiles and k ≥ 1.");
        int dim = profiles[0].Length;
        var x = profiles.Select(p => p.Length == dim ? (normalize ? NormalizeShape(p) : (double[])p.Clone()) : throw new ArgumentException("Profiles must have equal length.")).ToArray();
        var rng = new Random(seed);
        ClusteringResult? best = null;
        for (int r = 0; r < Math.Max(1, restarts); r++)
        {
            var cent = PlusPlus(x, k, rng);
            var assign = new int[x.Length];
            int it = 0;
            for (; it < maxIterations; it++)
            {
                bool changed = it == 0;
                for (int i = 0; i < x.Length; i++)
                {
                    int bc = 0; double bd = double.PositiveInfinity;
                    for (int c = 0; c < k; c++) { double d = SquaredDistance(x[i], cent[c]); if (d < bd) { bd = d; bc = c; } }
                    if (assign[i] != bc) { assign[i] = bc; changed = true; }
                }
                if (!changed) break;
                for (int c = 0; c < k; c++)
                {
                    var members = Enumerable.Range(0, x.Length).Where(i => assign[i] == c).ToList();
                    if (members.Count == 0) { cent[c] = (double[])x[rng.Next(x.Length)].Clone(); continue; }
                    var m = new double[dim];
                    foreach (int i in members) for (int j = 0; j < dim; j++) m[j] += x[i][j];
                    for (int j = 0; j < dim; j++) m[j] /= members.Count;
                    cent[c] = m;
                }
            }
            double inertia = 0;
            for (int i = 0; i < x.Length; i++) inertia += SquaredDistance(x[i], cent[assign[i]]);
            if (best is null || inertia < best.Inertia - 1e-12) best = new ClusteringResult(assign, cent, inertia, it + 1, normalize);
        }
        return best!;
    }

    /// <summary>Divides a profile by its peak (shape in pu of peak).</summary>
    public static double[] NormalizeShape(ReadOnlySpan<double> p)
    {
        double max = 0;
        foreach (var v in p) max = Math.Max(max, Math.Abs(v));
        var r = p.ToArray();
        if (max > 0) for (int i = 0; i < r.Length; i++) r[i] /= max;
        return r;
    }

    internal static double SquaredDistance(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; s += d * d; }
        return s;
    }

    private static double[][] PlusPlus(double[][] x, int k, Random rng)
    {
        var cent = new double[k][];
        cent[0] = (double[])x[rng.Next(x.Length)].Clone();
        var d2 = x.Select(p => SquaredDistance(p, cent[0])).ToArray();
        for (int c = 1; c < k; c++)
        {
            double sum = d2.Sum(), u = rng.NextDouble() * sum;
            int pick = x.Length - 1;
            for (int i = 0; i < x.Length; i++) { u -= d2[i]; if (u <= 0) { pick = i; break; } }
            cent[c] = (double[])x[pick].Clone();
            for (int i = 0; i < x.Length; i++) d2[i] = Math.Min(d2[i], SquaredDistance(x[i], cent[c]));
        }
        return cent;
    }
}
