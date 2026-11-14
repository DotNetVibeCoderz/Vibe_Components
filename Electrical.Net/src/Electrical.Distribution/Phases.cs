using System.Globalization;
using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Distribution;

/// <summary>Set of phases present at a bus or carried by an element (A = 1, B = 2, C = 4).</summary>
[Flags]
public enum PhaseSet
{
    None = 0,
    A = 1,
    B = 2,
    C = 4,
    AB = A | B,
    AC = A | C,
    BC = B | C,
    ABC = A | B | C,
}

/// <summary>Helpers for <see cref="PhaseSet"/>; phase indices are 0 = A, 1 = B, 2 = C.</summary>
public static class PhaseSetExtensions
{
    public static int Count(this PhaseSet p) => BitOperations.PopCount((uint)p & 7);

    public static bool Has(this PhaseSet p, int phase) => ((int)p & (1 << phase)) != 0;

    /// <summary>Phase indices in ascending order (A, B, C).</summary>
    public static int[] Indices(this PhaseSet p)
    {
        var r = new List<int>(3);
        for (int k = 0; k < 3; k++) if (p.Has(k)) r.Add(k);
        return [.. r];
    }

    public static PhaseSet FromIndices(IEnumerable<int> phases)
    {
        var s = PhaseSet.None;
        foreach (var k in phases)
        {
            if (k is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(phases), $"Phase index {k} must be 0..2.");
            s |= (PhaseSet)(1 << k);
        }
        return s;
    }

    /// <summary>Parses "abc", "AB", "c", "1.2.3" or "3.1" (OpenDSS node numbers).</summary>
    public static PhaseSet Parse(string text)
    {
        var s = PhaseSet.None;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            s |= ch switch
            {
                'a' or '1' => PhaseSet.A,
                'b' or '2' => PhaseSet.B,
                'c' or '3' => PhaseSet.C,
                '.' or ' ' or ',' or '0' or 'n' => PhaseSet.None,
                _ => throw new FormatException($"Invalid phase character '{ch}' in '{text}'."),
            };
        }
        return s;
    }

    public static string ToLabel(this PhaseSet p) =>
        p == PhaseSet.None ? "-" : string.Concat(p.Indices().Select(i => "ABC"[i]));
}

/// <summary>Small dense complex matrix helpers on <c>Complex[,]</c> (phase-frame 1×1 … 6×6 blocks).</summary>
internal static class CMat
{
    public static Complex[,] Zero(int n, int m) => new Complex[n, m];

    public static Complex[,] Identity(int n)
    {
        var r = new Complex[n, n];
        for (int i = 0; i < n; i++) r[i, i] = Complex.One;
        return r;
    }

    public static Complex[,] Mul(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), k = a.GetLength(1), m = b.GetLength(1);
        if (b.GetLength(0) != k) throw new ArgumentException("Dimension mismatch.");
        var r = new Complex[n, m];
        for (int i = 0; i < n; i++)
            for (int p = 0; p < k; p++)
            {
                var aip = a[i, p];
                if (aip == Complex.Zero) continue;
                for (int j = 0; j < m; j++) r[i, j] += aip * b[p, j];
            }
        return r;
    }

    public static Complex[] Mul(Complex[,] a, ReadOnlySpan<Complex> x)
    {
        int n = a.GetLength(0), m = a.GetLength(1);
        var r = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex s = 0;
            for (int j = 0; j < m; j++) s += a[i, j] * x[j];
            r[i] = s;
        }
        return r;
    }

    public static Complex[,] Add(Complex[,] a, Complex[,] b)
    {
        var r = (Complex[,])a.Clone();
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) r[i, j] += b[i, j];
        return r;
    }

    public static Complex[,] Sub(Complex[,] a, Complex[,] b)
    {
        var r = (Complex[,])a.Clone();
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) r[i, j] -= b[i, j];
        return r;
    }

    public static Complex[,] Scale(Complex[,] a, Complex s)
    {
        var r = (Complex[,])a.Clone();
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) r[i, j] *= s;
        return r;
    }

    public static Complex[,] Inverse(Complex[,] a)
    {
        int n = a.GetLength(0);
        if (n == 0) return new Complex[0, 0];
        if (n == 1) return new[,] { { Complex.One / a[0, 0] } };
        return ToArray(new DenseMatrix<Complex>(a).Inverse());
    }

    public static Complex[,] ToArray(DenseMatrix<Complex> d)
    {
        var r = new Complex[d.Rows, d.Cols];
        for (int i = 0; i < d.Rows; i++)
            for (int j = 0; j < d.Cols; j++) r[i, j] = d[i, j];
        return r;
    }

    public static DenseMatrix<Complex> ToDense(Complex[,] a) => new(a);

    /// <summary>Sub-block a[rows, cols].</summary>
    public static Complex[,] Block(Complex[,] a, int r0, int nr, int c0, int nc)
    {
        var r = new Complex[nr, nc];
        for (int i = 0; i < nr; i++)
            for (int j = 0; j < nc; j++) r[i, j] = a[r0 + i, c0 + j];
        return r;
    }

    public static Complex[,] Select(Complex[,] a, int[] rows, int[] cols)
    {
        var r = new Complex[rows.Length, cols.Length];
        for (int i = 0; i < rows.Length; i++)
            for (int j = 0; j < cols.Length; j++) r[i, j] = a[rows[i], cols[j]];
        return r;
    }

    /// <summary>Kron reduction: keeps the first <paramref name="keep"/> rows/columns, eliminating the rest
    /// (Z_ij − Z_in·Z_nn⁻¹·Z_nj).</summary>
    public static Complex[,] Kron(Complex[,] z, int keep)
    {
        int n = z.GetLength(0);
        if (keep == n) return (Complex[,])z.Clone();
        var zij = Block(z, 0, keep, 0, keep);
        var zin = Block(z, 0, keep, keep, n - keep);
        var znj = Block(z, keep, n - keep, 0, keep);
        var znn = Block(z, keep, n - keep, keep, n - keep);
        return Sub(zij, Mul(Mul(zin, Inverse(znn)), znj));
    }

    public static double MaxAbs(Complex[,] a)
    {
        double m = 0;
        foreach (var v in a) m = Math.Max(m, v.Magnitude);
        return m;
    }

    public static string Format(Complex[,] a, string fmt = "0.0000")
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < a.GetLength(0); i++)
        {
            for (int j = 0; j < a.GetLength(1); j++)
            {
                var v = a[i, j];
                sb.Append(v.Real.ToString(fmt, CultureInfo.InvariantCulture))
                  .Append(v.Imaginary >= 0 ? "+j" : "-j")
                  .Append(Math.Abs(v.Imaginary).ToString(fmt, CultureInfo.InvariantCulture)).Append("  ");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
