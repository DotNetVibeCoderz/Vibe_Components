using System.Runtime.CompilerServices;
using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Core.Network;

/// <summary>Branch π-model admittances (MATPOWER convention, pu): I_f = Yff·V_f + Yft·V_t, I_t = Ytf·V_f + Ytt·V_t.</summary>
public readonly record struct BranchAdmittance(Complex Yff, Complex Yft, Complex Ytf, Complex Ytt)
{
    public static BranchAdmittance Of(Branch br, Sequence sequence = Sequence.Positive)
    {
        double r = sequence == Sequence.Zero ? br.ZeroSeqR : br.R;
        double x = sequence == Sequence.Zero ? br.ZeroSeqX : br.X;
        double b = sequence == Sequence.Zero ? br.ZeroSeqB : br.B;
        var ys = 1 / new Complex(r, x);
        var bc = new Complex(0, b / 2);
        double shift = sequence == Sequence.Negative ? -br.ShiftDeg : br.ShiftDeg;
        var t = Complex.FromPolarCoordinates(br.EffectiveTap, shift * Scalar.Deg2Rad);
        var ytt = ys + bc;
        return new BranchAdmittance(ytt / (t * Complex.Conjugate(t)), -ys / Complex.Conjugate(t), -ys / t, ytt);
    }
}

public enum Sequence { Positive, Negative, Zero }

/// <summary>Bus admittance matrix and per-branch admittances for one network snapshot.</summary>
public sealed class AdmittanceMatrix
{
    internal AdmittanceMatrix(PowerNetwork network, SparseMatrix<Complex> y, BranchAdmittance[] branches)
    {
        Network = network;
        Y = y;
        Branches = branches;
    }

    public PowerNetwork Network { get; }
    /// <summary>Y-bus (pu) indexed by bus position.</summary>
    public SparseMatrix<Complex> Y { get; }
    /// <summary>Per-branch admittances, aligned with <see cref="PowerNetwork.Branches"/> (zero for out-of-service branches).</summary>
    public BranchAdmittance[] Branches { get; }

    /// <summary>Builds the positive-sequence Y-bus including bus shunts.</summary>
    public static AdmittanceMatrix Build(PowerNetwork net, Sequence sequence = Sequence.Positive)
    {
        int n = net.BusCount;
        var b = new SparseBuilder<Complex>(n, n);
        var adm = new BranchAdmittance[net.Branches.Count];
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            if (!br.InService) continue;
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            var a = BranchAdmittance.Of(br, sequence);
            if (sequence == Sequence.Zero && br.IsTransformer)
                a = ZeroSequenceTransformer(br, a, b, f, t);
            adm[k] = a;
            b.Add(f, f, a.Yff); b.Add(f, t, a.Yft);
            b.Add(t, f, a.Ytf); b.Add(t, t, a.Ytt);
        }
        for (int i = 0; i < n; i++)
        {
            var bus = net.Buses[i];
            b.Add(i, i, new Complex(bus.Gs, bus.Bs) / net.BaseMva); // also ensures every diagonal exists
        }
        return new AdmittanceMatrix(net, b.ToCsr(), adm);
    }

    // Zero-sequence transformer models: series path only for YN-yn; YN-d grounds the YN side through the leakage
    // impedance; other connections block zero-sequence current.
    private static BranchAdmittance ZeroSequenceTransformer(Branch br, BranchAdmittance a, SparseBuilder<Complex> b, int f, int t)
    {
        var ys = 1 / new Complex(br.ZeroSeqR, br.ZeroSeqX);
        switch (br.Connection)
        {
            case WindingConnection.YNyn: return a;
            case WindingConnection.YNd: b.Add(f, f, ys); return default;
            case WindingConnection.Dyn: b.Add(t, t, ys); return default;
            default: return default;
        }
    }

    /// <summary>Complex power injections S = V ∘ conj(Y·V) [pu].</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public Complex[] Injections(ReadOnlySpan<Complex> v)
    {
        var i = Y.Multiply(v);
        var s = new Complex[v.Length];
        for (int k = 0; k < v.Length; k++) s[k] = v[k] * Complex.Conjugate(i[k]);
        return s;
    }
}
