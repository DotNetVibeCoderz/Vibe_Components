using System.Numerics;

namespace Electrical.Distribution;

/// <summary>
/// Kersting's generalized matrices of a series element between node n (source side) and node m (load side), in the element's phase order:
/// backward sweep V_n = a·V_m + b·I_m, I_n = c·V_m + d·I_m; forward sweep V_m = A·V_n − B·I_m.
/// Property names: <see cref="Sa"/> = a, <see cref="Sb"/> = b, <see cref="Sc"/> = c, <see cref="Sd"/> = d, <see cref="A"/>, <see cref="B"/>.
/// Units: V, A, Ω, S. <see cref="Sa"/>/<see cref="Sb"/> are null for delta connections where they are not unique.
/// </summary>
public sealed class GeneralizedMatrices
{
    internal GeneralizedMatrices(Complex[,]? a, Complex[,]? b, Complex[,] c, Complex[,] d, Complex[,] A, Complex[,] B)
    {
        Sa = a; Sb = b; Sc = c; Sd = d; this.A = A; this.B = B;
    }

    /// <summary>a (voltage gain, backward sweep).</summary>
    public Complex[,]? Sa { get; }
    /// <summary>b [Ω] (backward sweep).</summary>
    public Complex[,]? Sb { get; }
    /// <summary>c [S] (current from voltage, backward sweep).</summary>
    public Complex[,] Sc { get; }
    /// <summary>d (current gain, backward sweep).</summary>
    public Complex[,] Sd { get; }
    /// <summary>A (forward sweep voltage gain).</summary>
    public Complex[,] A { get; }
    /// <summary>B [Ω] (forward sweep).</summary>
    public Complex[,] B { get; }

    /// <summary>
    /// Line segment (Kersting eqs. 6.10–6.21): a = U + ½·Z·Y, b = Z, c = Y + ¼·Y·Z·Y, d = a, A = a⁻¹, B = a⁻¹·b, with total
    /// series impedance Z [Ω] and total shunt admittance Y [S].
    /// </summary>
    public static GeneralizedMatrices ForLine(Complex[,] zOhm, Complex[,] ySiemens)
    {
        int n = zOhm.GetLength(0);
        var u = CMat.Identity(n);
        var zy = CMat.Mul(zOhm, ySiemens);
        var a = CMat.Add(u, CMat.Scale(zy, 0.5));
        var c = CMat.Add(ySiemens, CMat.Scale(CMat.Mul(ySiemens, zy), 0.25));
        var ainv = CMat.Inverse(a);
        return new(a, (Complex[,])zOhm.Clone(), c, (Complex[,])a.Clone(), ainv, CMat.Mul(ainv, zOhm));
    }

    /// <summary>Line segment from a line code and length [km].</summary>
    public static GeneralizedMatrices ForLine(LineSegment line) =>
        ForLine(CMat.Scale(line.Code.ZOhmPerKm, Math.Max(line.LengthKm, 1e-9)), CMat.Scale(line.Code.YSiemensPerKm, line.LengthKm));

    /// <summary>Transformer bank (any supported connection), derived from its nodal admittance; c = 0 as in Kersting chapter 8.</summary>
    public static GeneralizedMatrices ForTransformer(DistributionTransformer transformer) =>
        FromAdmittance(ElementModels.Transformer(transformer, 0, 1).Y, transformer.Phases.Count(), forceTransformer: true);

    /// <summary>Step-voltage regulator (wye), derived from its nodal admittance.</summary>
    public static GeneralizedMatrices ForRegulator(VoltageRegulator regulator) =>
        FromAdmittance(ElementModels.Regulator(regulator, 0, 1).Y, regulator.Phases.Count(), forceTransformer: true);

    /// <summary>
    /// From a 2n×2n nodal admittance [[Ynn, Ynm], [Ymn, Ymm]]: A = −Ymm⁻¹·Ymn, B = Ymm⁻¹, d = −Ynm·Ymm⁻¹, c = 0 for transformer-like
    /// elements; a = −Ymn⁻¹·Ymm, b = −Ymn⁻¹ when Ymn is invertible.
    /// </summary>
    internal static GeneralizedMatrices FromAdmittance(Complex[,] y, int n, bool forceTransformer)
    {
        var ynn = CMat.Block(y, 0, n, 0, n);
        var ynm = CMat.Block(y, 0, n, n, n);
        var ymn = CMat.Block(y, n, n, 0, n);
        var ymm = CMat.Block(y, n, n, n, n);
        var ymmInv = CMat.Inverse(ymm);
        var A = CMat.Scale(CMat.Mul(ymmInv, ymn), -1);
        var d = CMat.Scale(CMat.Mul(ynm, ymmInv), -1);
        Complex[,]? a = null, b = null;
        Complex[,] c = new Complex[n, n];
        if (IsInvertible(ymn))
        {
            var ymnInv = CMat.Inverse(ymn);
            a = CMat.Scale(CMat.Mul(ymnInv, ymm), -1);
            b = CMat.Scale(ymnInv, -1);
            if (!forceTransformer)
            {
                c = CMat.Sub(ynm, CMat.Mul(CMat.Mul(ynn, ymnInv), ymm));
                d = CMat.Scale(CMat.Mul(ynn, ymnInv), -1);
            }
        }
        return new(a, b, c, d, A, ymmInv);
    }

    private static bool IsInvertible(Complex[,] m)
    {
        int n = m.GetLength(0);
        double scale = CMat.MaxAbs(m);
        if (scale == 0) return false;
        // Gaussian elimination with partial pivoting to estimate rank
        var a = (Complex[,])m.Clone();
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (a[i, k].Magnitude > a[p, k].Magnitude) p = i;
            if (a[p, k].Magnitude < 1e-7 * scale) return false;
            for (int j = 0; j < n; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);
            for (int i = k + 1; i < n; i++)
            {
                var f = a[i, k] / a[k, k];
                for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
            }
        }
        return true;
    }
}

/// <summary>Nodal model of a series element: Y over [from-side phases…, to-side phases…] with ground as reference.</summary>
internal sealed class BranchModel
{
    public required string Name { get; init; }
    public required DistributionBranchKind Kind { get; init; }
    public required object Element { get; init; }
    public required int FromBus { get; init; }
    public required int ToBus { get; init; }
    public required int[] FromPhases { get; init; }
    public required int[] ToPhases { get; init; }
    /// <summary>Primitive admittance [S], size (nf + nt)².</summary>
    public required Complex[,] Y { get; init; }
    public int Nf => FromPhases.Length;
    public int Nt => ToPhases.Length;
}

internal static class ElementModels
{
    /// <summary>Relative size of the shunt that ties floating (delta / ungrounded) windings to ground.</summary>
    // Reference shunt that keeps delta / ungrounded-wye windings non-singular. It is a pure susceptance (no active-power loss)
    // and tiny relative to the winding admittance: a real conductance here used to absorb tens of kW on stiff substation
    // transformers (IEEE 13-node 'Sub', |y| ≈ 3600 S → 20.7 kW).
    private const double FloatingShunt = 1e-9;

    public static BranchModel Line(LineSegment l, int from, int to)
    {
        int n = l.PhaseOrder.Length;
        double len = Math.Max(l.LengthKm, 1e-9);
        var ys = CMat.Inverse(CMat.Scale(l.Code.ZOhmPerKm, len));
        var ysh = CMat.Scale(l.Code.YSiemensPerKm, l.LengthKm / 2);
        var y = new Complex[2 * n, 2 * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                y[i, j] = ys[i, j] + ysh[i, j];
                y[n + i, n + j] = ys[i, j] + ysh[i, j];
                y[i, n + j] = -ys[i, j];
                y[n + i, j] = -ys[i, j];
            }
        return new BranchModel
        {
            Name = l.Name, Kind = DistributionBranchKind.Line, Element = l, FromBus = from, ToBus = to,
            FromPhases = l.PhaseOrder, ToPhases = l.PhaseOrder, Y = y,
        };
    }

    public static BranchModel Transformer(DistributionTransformer t, int from, int to)
    {
        var phases = t.Phases.Indices();
        int np = phases.Length;
        bool threePhase = np == 3;
        if (!threePhase && t.Connection != TransformerConnection.WyeGWyeG)
            throw new NotSupportedException($"Transformer {t.Name}: partial-phase banks must be grounded wye – grounded wye.");
        bool priDelta = t.Connection is TransformerConnection.DeltaWyeG or TransformerConnection.DeltaDelta;
        bool secDelta = t.Connection is TransformerConnection.WyeDelta or TransformerConnection.WyeGDelta or TransformerConnection.DeltaDelta;
        bool priFloatingWye = t.Connection == TransformerConnection.WyeDelta;
        double vp = threePhase && !priDelta ? t.KvPrimary / Math.Sqrt(3) : t.KvPrimary;
        double vs = threePhase && !secDelta ? t.KvSecondary / Math.Sqrt(3) : t.KvSecondary;
        double kvaUnit = t.KvaRating / np;
        double turns = vp * t.TapPrimary / (vs * t.TapSecondary);
        var zUnit = new Complex(t.RPercent, t.XPercent) / 100 * (vs * vs * 1000 / kvaUnit);
        var yUnit = Complex.One / zUnit;
        int nTerm = 2 * np + (priFloatingWye ? 1 : 0);
        int neutral = 2 * np;
        var y = new Complex[nTerm, nTerm];
        for (int k = 0; k < np; k++)
        {
            (int pp, int pm) = priDelta ? (k, t.Connection == TransformerConnection.DeltaDelta ? (k + 1) % 3 : (k + 2) % 3)
                : (k, priFloatingWye ? neutral : -1);
            (int sp, int sm) = secDelta ? (np + k, np + (k + 1) % 3) : (np + k, -1);
            var g = new (int Node, Complex Coef)[] { (pp, 1 / turns), (pm, -1 / turns), (sp, -1), (sm, 1) };
            foreach (var (ni, ci) in g)
            {
                if (ni < 0) continue;
                foreach (var (nj, cj) in g)
                    if (nj >= 0) y[ni, nj] += yUnit * ci * cj;
            }
        }
        var reg = new Complex(0, FloatingShunt * yUnit.Magnitude);
        if (priDelta) for (int k = 0; k < np; k++) y[k, k] += reg / (turns * turns);
        if (secDelta) for (int k = 0; k < np; k++) y[np + k, np + k] += reg;
        if (priFloatingWye) y = CMat.Kron(y, 2 * np);
        return new BranchModel
        {
            Name = t.Name, Kind = DistributionBranchKind.Transformer, Element = t, FromBus = from, ToBus = to,
            FromPhases = phases, ToPhases = phases, Y = y,
        };
    }

    public static BranchModel Regulator(VoltageRegulator r, int from, int to)
    {
        var phases = r.Phases.Indices();
        int np = phases.Length;
        var y = new Complex[2 * np, 2 * np];
        var zUnit = new Complex(r.RPercent, r.XPercent) / 100 * (r.KvLN * r.KvLN * 1000 / r.KvaPerPhase);
        var yUnit = Complex.One / zUnit;
        for (int k = 0; k < np; k++)
        {
            double t = 1 / r.Ratio(phases[k]);   // V_in / V_out
            y[k, k] += yUnit / (t * t);
            y[k, np + k] -= yUnit / t;
            y[np + k, k] -= yUnit / t;
            y[np + k, np + k] += yUnit;
        }
        return new BranchModel
        {
            Name = r.Name, Kind = DistributionBranchKind.Regulator, Element = r, FromBus = from, ToBus = to,
            FromPhases = phases, ToPhases = phases, Y = y,
        };
    }
}
