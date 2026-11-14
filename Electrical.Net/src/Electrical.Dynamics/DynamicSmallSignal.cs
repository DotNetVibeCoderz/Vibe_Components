using System.Numerics;
using Electrical.Core.Numerics;
using Electrical.Stability;

namespace Electrical.Dynamics;

/// <summary>One row of a mode table.</summary>
/// <param name="Eigenvalue">λ = σ + jω [1/s].</param>
/// <param name="FrequencyHz">Damped frequency [Hz].</param>
/// <param name="DampingRatio">ζ = −σ/|λ|.</param>
/// <param name="Dominant">States with the largest participation factors (name, normalised participation), descending.</param>
/// <param name="RotorParticipation">Sum of the participation of rotor-angle and speed states (≈ 1 for electromechanical modes).</param>
/// <param name="SpeedShape">Right-eigenvector components of the machine speed states per unit name (mode shape), normalised to the largest.</param>
public sealed record ModeInfo(Complex Eigenvalue, double FrequencyHz, double DampingRatio, IReadOnlyList<(string State, double Participation)> Dominant,
    double RotorParticipation, IReadOnlyDictionary<string, Complex> SpeedShape)
{
    public override string ToString() =>
        $"λ = {Eigenvalue.Real:0.####} ± j{Math.Abs(Eigenvalue.Imaginary):0.####}  f = {FrequencyHz:0.###} Hz  ζ = {100 * DampingRatio:0.##} %  [{string.Join(", ", Dominant.Take(3).Select(d => $"{d.State} {d.Participation:0.00}"))}]";
}

/// <summary>Small-signal result of the detailed dynamic model.</summary>
public sealed class DynamicSmallSignalResult
{
    /// <summary>Reduced state matrix A = f_x − f_V·g_V⁻¹·g_x (algebraic network variables eliminated).</summary>
    public required DenseMatrix<double> StateMatrix { get; init; }
    public required IReadOnlyList<string> States { get; init; }
    /// <summary>All eigenvalues sorted by decreasing real part.</summary>
    public required IReadOnlyList<Complex> Eigenvalues { get; init; }
    /// <summary>Oscillatory modes (positive imaginary part) with participation factors, sorted by frequency.</summary>
    public required IReadOnlyList<ModeInfo> Modes { get; init; }

    /// <summary>Oscillatory modes dominated by rotor angle/speed states (participation ≥ <paramref name="minRotorParticipation"/>), 0.1–3 Hz.</summary>
    public IReadOnlyList<ModeInfo> ElectromechanicalModes(double minRotorParticipation = 0.3) =>
        Modes.Where(m => m.RotorParticipation >= minRotorParticipation && m.FrequencyHz is > 0.1 and < 3).OrderBy(m => m.FrequencyHz).ToList();

    /// <summary>True when no eigenvalue has a real part above <paramref name="tolerance"/> [1/s].</summary>
    public bool IsStable(double tolerance = 1e-4) => Eigenvalues.All(l => l.Real <= tolerance);

    /// <summary>Text table of the oscillatory modes.</summary>
    public string ModeTable() => string.Join(Environment.NewLine, Modes.Select(m => m.ToString()));
}

/// <summary>Linearisation of the full dynamic model around its initialized equilibrium.</summary>
public static class DynamicSmallSignal
{
    /// <summary>
    /// Numerical linearisation: each column of A is (f(x + εeⱼ, V(x + εeⱼ)) − f(x − εeⱼ, V(x − εeⱼ)))/2ε with the network re-solved
    /// exactly for every perturbation (so the algebraic variables are eliminated). Eigenvalues by <see cref="EigenSolver"/>, participation
    /// factors for oscillatory modes above <paramref name="minFrequencyHz"/>.
    /// </summary>
    public static DynamicSmallSignalResult Analyze(DynamicSystem system, double minFrequencyHz = 0.05, double relativeStep = 1e-6)
    {
        system.Reset();
        int n = system.StateCount;
        var a = new DenseMatrix<double>(n, n);
        var x0 = system.InitialState;
        var xp = (double[])x0.Clone();
        var fp = new double[n]; var fm = new double[n];
        for (int j = 0; j < n; j++)
        {
            double eps = relativeStep * Math.Max(1, Math.Abs(x0[j]));
            xp[j] = x0[j] + eps;
            Eval(system, xp, fp);
            xp[j] = x0[j] - eps;
            Eval(system, xp, fm);
            xp[j] = x0[j];
            for (int i = 0; i < n; i++) a[i, j] = (fp[i] - fm[i]) / (2 * eps);
        }
        var eig = EigenSolver.Eigenvalues(a);
        var names = system.StateNames;
        var rotor = new bool[n];
        var speedIdx = new List<(string Unit, int Index)>();
        foreach (var u in system.Units)
        {
            if (u is SynchronousMachineUnit mu)
            {
                rotor[u.StateOffset + mu.Machine.AngleState] = true;
                rotor[u.StateOffset + mu.Machine.SpeedState] = true;
                if (!u.IsInfiniteBus) speedIdx.Add((u.Name, u.StateOffset + mu.Machine.SpeedState));
            }
            else if (u is GridFormingUnit gu)
            {
                rotor[u.StateOffset + gu.Inverter.AngleState] = true;
            }
        }
        var modes = new List<ModeInfo>();
        foreach (var l in eig)
        {
            if (l.Imaginary <= 1e-6) continue;
            var om = new OscillationMode(l, null);
            if (om.FrequencyHz < minFrequencyHz) continue;
            var p = EigenSolver.ParticipationFactors(a, l);
            var v = EigenSolver.RightEigenvector(a, l);
            double rp = 0;
            for (int k = 0; k < n; k++) if (rotor[k]) rp += p[k];
            var dom = Enumerable.Range(0, n).OrderByDescending(k => p[k]).Take(6).Select(k => (names[k], p[k])).ToList();
            double vmax = speedIdx.Count == 0 ? 1 : speedIdx.Max(s => v[s.Index].Magnitude);
            var shape = speedIdx.ToDictionary(s => s.Unit, s => vmax > 0 ? v[s.Index] / vmax : Complex.Zero);
            modes.Add(new ModeInfo(l, om.FrequencyHz, om.DampingRatio, dom, rp, shape));
        }
        return new DynamicSmallSignalResult
        {
            StateMatrix = a, States = names, Eigenvalues = eig, Modes = modes.OrderBy(m => m.FrequencyHz).ToList(),
        };
    }

    private static void Eval(DynamicSystem s, double[] x, double[] f)
    {
        var v = (Complex[])s.InitialVoltages.Clone();
        s.SolveNetwork(x, v, 1e-13, 200);
        s.Derivatives(0, x, v, f);
    }
}
