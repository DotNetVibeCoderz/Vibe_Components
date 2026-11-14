using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;

namespace Electrical.Stability;

/// <summary>One eigenvalue (mode) of the linearised system.</summary>
/// <param name="Eigenvalue">λ = σ + jω [1/s].</param>
/// <param name="Participation">Normalised participation factors of the states (null when not computed).</param>
public sealed record OscillationMode(Complex Eigenvalue, double[]? Participation)
{
    /// <summary>Damped frequency f = ω/2π [Hz].</summary>
    public double FrequencyHz => Math.Abs(Eigenvalue.Imaginary) / (2 * Math.PI);
    /// <summary>Damping ratio ζ = −σ/|λ| (1 for real stable modes).</summary>
    public double DampingRatio => Eigenvalue.Magnitude < 1e-12 ? 0 : -Eigenvalue.Real / Eigenvalue.Magnitude;
    public bool IsOscillatory => Eigenvalue.Imaginary > 1e-6;
}

/// <summary>Small-signal analysis result for the classical multi-machine model.</summary>
public sealed class SmallSignalResult
{
    public required DenseMatrix<double> StateMatrix { get; init; }
    /// <summary>State names (Δδ_i, Δω_i).</summary>
    public required IReadOnlyList<string> States { get; init; }
    /// <summary>All modes, sorted by decreasing real part.</summary>
    public required IReadOnlyList<OscillationMode> Modes { get; init; }

    /// <summary>Oscillatory modes (positive imaginary part), e.g. local and inter-area electromechanical modes.</summary>
    public IEnumerable<OscillationMode> OscillatoryModes => Modes.Where(m => m.IsOscillatory);

    /// <summary>
    /// True when no eigenvalue has a positive real part beyond <paramref name="tolerance"/>
    /// (the classical model always has a zero mode from the angle reference, which is ignored).
    /// </summary>
    public bool IsStable(double tolerance = 1e-6) => Modes.All(m => m.Eigenvalue.Real <= tolerance);

    /// <summary>Smallest damping ratio among oscillatory modes.</summary>
    public double MinimumDampingRatio => OscillatoryModes.Select(m => m.DampingRatio).DefaultIfEmpty(1).Min();
}

/// <summary>Linearised classical model: ẋ = A·x with x = [Δδ; Δω].</summary>
public static class SmallSignal
{
    /// <summary>
    /// State matrix A: dΔδ_i/dt = ω_s·Δω_i; dΔω_i/dt = −(Σ_j K_ij·Δδ_j + D_i·Δω_i)/(2H_i),
    /// K_ij = ∂P_e,i/∂δ_j = E_iE_j(G_ij sin δ_ij − B_ij cos δ_ij), K_ii = −Σ_{j≠i} K_ij. Infinite-bus machines are not states.
    /// </summary>
    public static SmallSignalResult Analyze(ClassicalModel model, bool participation = true)
    {
        var ms = model.Machines;
        var yred = model.ReducedAdmittance();
        var dyn = Enumerable.Range(0, ms.Count).Where(i => !ms[i].Infinite).ToArray();
        int n = dyn.Length;
        var k = new double[ms.Count, ms.Count];
        for (int i = 0; i < ms.Count; i++)
        {
            double sum = 0;
            for (int j = 0; j < ms.Count; j++)
            {
                if (i == j) continue;
                double dij = ms[i].Delta0Rad - ms[j].Delta0Rad;
                var y = yred[i, j];
                k[i, j] = ms[i].E * ms[j].E * (y.Real * Math.Sin(dij) - y.Imaginary * Math.Cos(dij));
                sum += k[i, j];
            }
            k[i, i] = -sum;
        }
        var a = new DenseMatrix<double>(2 * n, 2 * n);
        double ws = model.OmegaS;
        for (int p = 0; p < n; p++)
        {
            int i = dyn[p];
            a[p, n + p] = ws;
            for (int q = 0; q < n; q++) a[n + p, q] = -k[i, dyn[q]] / (2 * ms[i].H);
            a[n + p, n + p] = -ms[i].D / (2 * ms[i].H);
        }
        var eig = EigenSolver.Eigenvalues(a);
        var modes = eig.Select(l => new OscillationMode(l, participation && l.Imaginary > 1e-6 ? EigenSolver.ParticipationFactors(a, l) : null)).ToList();
        var states = dyn.Select(i => $"Δδ {ms[i].Name}").Concat(dyn.Select(i => $"Δω {ms[i].Name}")).ToList();
        return new SmallSignalResult { StateMatrix = a, States = states, Modes = modes };
    }

    /// <summary>Builds the classical model from a power flow and analyses it.</summary>
    public static SmallSignalResult Analyze(PowerNetwork network, PowerFlowResult? powerFlow = null) =>
        Analyze(ClassicalModel.FromPowerFlow(network, powerFlow));
}
