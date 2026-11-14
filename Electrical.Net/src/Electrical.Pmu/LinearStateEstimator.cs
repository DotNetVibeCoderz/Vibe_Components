using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.Pmu.C37118;
using Electrical.PowerFlow;

namespace Electrical.Pmu;

/// <summary>
/// One phasor measurement in per unit (network <see cref="PowerNetwork.BaseMva"/> and bus base kV): a bus voltage, or a branch current
/// leaving <see cref="PmuChannelBinding.BusId"/>. <paramref name="Sigma"/> is the standard deviation of the complex error [pu].
/// </summary>
public sealed record PhasorMeasurement(PmuChannelBinding Channel, Complex Value, double Sigma);

/// <summary>Result of <see cref="LinearStateEstimator.Estimate"/>. Voltages in pu, angles in degrees in the PMU (UTC) reference.</summary>
public sealed class LinearStateEstimate
{
    public required PowerNetwork Network { get; init; }
    /// <summary>Estimated complex bus voltages [pu] by bus position.</summary>
    public required Complex[] Voltages { get; init; }
    /// <summary>Measurement residuals z − H·x̂ [pu] in measurement order.</summary>
    public required Complex[] Residuals { get; init; }
    /// <summary>Weighted sum of squared residuals J = Σ |r|²/σ².</summary>
    public required double Objective { get; init; }
    /// <summary>Degrees of freedom of J: 2·(measurements − buses) real equations.</summary>
    public required int DegreesOfFreedom { get; init; }
    public double Vm(int busId) => Voltages[Network.IndexOf(busId)].Magnitude;
    public double VaDeg(int busId) => Voltages[Network.IndexOf(busId)].Phase * 180 / Math.PI;
    /// <summary>Voltages rotated so that bus <paramref name="referenceBusId"/> has angle <paramref name="referenceAngleDeg"/>.</summary>
    public Complex[] Referenced(int referenceBusId, double referenceAngleDeg = 0)
    {
        double rot = referenceAngleDeg * Math.PI / 180 - Voltages[Network.IndexOf(referenceBusId)].Phase;
        var r = Complex.FromPolarCoordinates(1, rot);
        return Voltages.Select(v => v * r).ToArray();
    }
}

/// <summary>
/// Linear (non-iterative) PMU state estimator: with voltage phasors V_i and branch current phasors I = Y_ff·V_f + Y_ft·V_t (π model
/// of <see cref="AdmittanceMatrix"/>) the model z = H·V is linear in the complex bus voltages, so x̂ = (Hᴴ W H)⁻¹ Hᴴ W z with W = diag(1/σ²).
/// Requires full observability by PMUs (every bus measured directly or reached through a measured branch current from a measured bus).
/// </summary>
public static class LinearStateEstimator
{
    /// <summary>Solves the complex WLS problem; throws <see cref="InvalidOperationException"/> when the PMU set does not observe every bus.</summary>
    public static LinearStateEstimate Estimate(PowerNetwork net, IReadOnlyList<PhasorMeasurement> measurements)
    {
        int n = net.BusCount, m = measurements.Count;
        var y = AdmittanceMatrix.Build(net);
        var rows = new (int Col, Complex H)[m][];
        for (int r = 0; r < m; r++)
        {
            var ch = measurements[r].Channel;
            if (ch.Kind == PhasorKind.Voltage) rows[r] = [(net.IndexOf(ch.BusId), Complex.One)];
            else
            {
                var br = net.Branches[ch.BranchIndex];
                var a = y.Branches[ch.BranchIndex];
                int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
                rows[r] = ch.End == BranchTerminal.From ? [(f, a.Yff), (t, a.Yft)] : [(f, a.Ytf), (t, a.Ytt)];
            }
        }
        if (!IsObservable(net, measurements)) throw new InvalidOperationException("The PMU measurement set does not observe every bus.");
        var g = new DenseMatrix<Complex>(n, n);
        var rhs = new Complex[n];
        for (int r = 0; r < m; r++)
        {
            double w = 1 / (measurements[r].Sigma * measurements[r].Sigma);
            foreach (var (ci, hi) in rows[r])
            {
                rhs[ci] += Complex.Conjugate(hi) * w * measurements[r].Value;
                foreach (var (cj, hj) in rows[r]) g[ci, cj] += Complex.Conjugate(hi) * w * hj;
            }
        }
        var x = g.Solve(rhs);
        var res = new Complex[m];
        double j = 0;
        for (int r = 0; r < m; r++)
        {
            var hx = Complex.Zero;
            foreach (var (c, h) in rows[r]) hx += h * x[c];
            res[r] = measurements[r].Value - hx;
            j += res[r].Magnitude * res[r].Magnitude / (measurements[r].Sigma * measurements[r].Sigma);
        }
        return new LinearStateEstimate { Network = net, Voltages = x, Residuals = res, Objective = j, DegreesOfFreedom = 2 * (m - n) };
    }

    /// <summary>Topological observability: every bus is voltage-measured or linked to a measured bus by a measured branch current.</summary>
    public static bool IsObservable(PowerNetwork net, IEnumerable<PhasorMeasurement> measurements)
    {
        var known = new HashSet<int>();
        var currents = new List<PmuChannelBinding>();
        foreach (var mm in measurements)
        {
            if (mm.Channel.Kind == PhasorKind.Voltage) known.Add(mm.Channel.BusId);
            else currents.Add(mm.Channel);
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var c in currents)
            {
                var br = net.Branches[c.BranchIndex];
                // I = Y_near·V_near + Y_far·V_far: one known terminal voltage determines the other (non-zero series admittance)
                if (known.Contains(br.From) || known.Contains(br.To))
                    changed |= known.Add(br.From) | known.Add(br.To);
            }
        }
        return net.Buses.All(b => known.Contains(b.Id));
    }

    /// <summary>
    /// Converts a decoded data frame into per-unit measurements using the channel bindings (e.g. <see cref="PmuSimulator.Bindings"/>).
    /// Voltages are V line-to-neutral, currents A; σ values are in pu.
    /// </summary>
    public static List<PhasorMeasurement> FromFrame(PowerNetwork net, C37DataFrame frame, IReadOnlyList<IReadOnlyList<PmuChannelBinding>> bindings,
        double sigmaVoltagePu = 0.001, double sigmaCurrentPu = 0.002)
    {
        var list = new List<PhasorMeasurement>();
        for (int p = 0; p < frame.Blocks.Count; p++)
        {
            var b = frame.Blocks[p];
            for (int i = 0; i < b.Phasors.Count; i++)
            {
                var ch = bindings[p][i];
                double kv = net.GetBus(ch.BusId).BaseKv;
                if (ch.Kind == PhasorKind.Voltage)
                    list.Add(new PhasorMeasurement(ch, b.Phasors[i] / (kv * 1000 / Math.Sqrt(3)), sigmaVoltagePu));
                else
                    list.Add(new PhasorMeasurement(ch, b.Phasors[i] / (net.BaseMva * 1e6 / (Math.Sqrt(3) * kv * 1000)), sigmaCurrentPu));
            }
        }
        return list;
    }

    /// <summary>
    /// Synthesises PMU measurements [pu] from a power-flow solution: voltage at each PMU bus and current of every incident in-service
    /// branch, with deterministic complex Gaussian noise of σ per component scaled by <paramref name="noise"/>.
    /// </summary>
    public static List<PhasorMeasurement> FromPowerFlow(PowerFlowResult pf, IEnumerable<int> pmuBuses, double sigmaVoltagePu = 0.001,
        double sigmaCurrentPu = 0.002, double noise = 1.0, int seed = 2026)
    {
        var net = pf.Network;
        var y = AdmittanceMatrix.Build(net);
        var rng = new Random(seed);
        double G() { double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble(); return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2); }
        Complex Noise(double s) => new Complex(G(), G()) * (s * noise / Math.Sqrt(2));
        var list = new List<PhasorMeasurement>();
        foreach (int bus in pmuBuses)
        {
            var ch = new PmuChannelBinding(PhasorKind.Voltage, bus);
            list.Add(new PhasorMeasurement(ch, pf.Voltages[net.IndexOf(bus)] + Noise(sigmaVoltagePu), sigmaVoltagePu));
            for (int k = 0; k < net.Branches.Count; k++)
            {
                var br = net.Branches[k];
                if (!br.InService || (br.From != bus && br.To != bus)) continue;
                var end = br.From == bus ? BranchTerminal.From : BranchTerminal.To;
                var i = PmuSimulator.BranchCurrent(net, y, pf.Voltages, k, end);
                list.Add(new PhasorMeasurement(new PmuChannelBinding(PhasorKind.Current, bus, k, end), i + Noise(sigmaCurrentPu), sigmaCurrentPu));
            }
        }
        return list;
    }
}
