using System.Runtime.CompilerServices;
using System.Numerics;

namespace Electrical.Core.Numerics;

/// <summary>Row-major dense matrix over double or <see cref="Complex"/>.</summary>
public sealed class DenseMatrix<T> where T : INumberBase<T>
{
    private readonly T[] _data;

    public DenseMatrix(int rows, int cols)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        Rows = rows;
        Cols = cols;
        _data = new T[rows * cols];
        if (!typeof(T).IsValueType || !EqualityComparer<T>.Default.Equals(default!, T.Zero)) Array.Fill(_data, T.Zero);
    }

    public DenseMatrix(T[,] values) : this(values.GetLength(0), values.GetLength(1))
    {
        for (int i = 0; i < Rows; i++)
            for (int j = 0; j < Cols; j++)
                _data[i * Cols + j] = values[i, j];
    }

    public int Rows { get; }
    public int Cols { get; }

    /// <summary>Raw row-major storage.</summary>
    public Span<T> Data => _data;

    public Span<T> Row(int i) => _data.AsSpan(i * Cols, Cols);

    public ref T this[int i, int j] => ref _data[i * Cols + j];

    public static DenseMatrix<T> Identity(int n)
    {
        var m = new DenseMatrix<T>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = T.One;
        return m;
    }

    public DenseMatrix<T> Clone()
    {
        var m = new DenseMatrix<T>(Rows, Cols);
        _data.AsSpan().CopyTo(m._data);
        return m;
    }

    public DenseMatrix<T> Transpose()
    {
        var t = new DenseMatrix<T>(Cols, Rows);
        for (int i = 0; i < Rows; i++)
            for (int j = 0; j < Cols; j++)
                t[j, i] = this[i, j];
        return t;
    }

    public T[] Multiply(ReadOnlySpan<T> x)
    {
        if (x.Length != Cols) throw new ArgumentException("Dimension mismatch.", nameof(x));
        var y = new T[Rows];
        for (int i = 0; i < Rows; i++)
        {
            var row = Row(i);
            T s = T.Zero;
            for (int j = 0; j < Cols; j++) s += row[j] * x[j];
            y[i] = s;
        }
        return y;
    }

    public static DenseMatrix<T> operator *(DenseMatrix<T> a, DenseMatrix<T> b)
    {
        if (a.Cols != b.Rows) throw new ArgumentException("Dimension mismatch.");
        var c = new DenseMatrix<T>(a.Rows, b.Cols);
        for (int i = 0; i < a.Rows; i++)
        {
            var ci = c.Row(i);
            for (int k = 0; k < a.Cols; k++)
            {
                T aik = a[i, k];
                if (aik == T.Zero) continue;
                var bk = b.Row(k);
                for (int j = 0; j < b.Cols; j++) ci[j] += aik * bk[j];
            }
        }
        return c;
    }

    /// <summary>Solves A·x = b with LU (partial pivoting). A is not modified.</summary>
    public T[] Solve(ReadOnlySpan<T> b) => new DenseLU<T>(this).Solve(b);

    /// <summary>Matrix inverse via LU.</summary>
    public DenseMatrix<T> Inverse()
    {
        if (Rows != Cols) throw new InvalidOperationException("Matrix must be square.");
        var lu = new DenseLU<T>(this);
        var inv = new DenseMatrix<T>(Rows, Cols);
        var e = new T[Rows];
        for (int j = 0; j < Cols; j++)
        {
            Array.Fill(e, T.Zero);
            e[j] = T.One;
            var col = lu.Solve(e);
            for (int i = 0; i < Rows; i++) inv[i, j] = col[i];
        }
        return inv;
    }

    public override string ToString() => $"DenseMatrix<{typeof(T).Name}>[{Rows}x{Cols}]";
}

/// <summary>LU factorization with partial pivoting (Doolittle, in-place copy).</summary>
public sealed class DenseLU<T> where T : INumberBase<T>
{
    private readonly DenseMatrix<T> _lu;
    private readonly int[] _perm;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public DenseLU(DenseMatrix<T> a)
    {
        if (a.Rows != a.Cols) throw new ArgumentException("Matrix must be square.", nameof(a));
        int n = a.Rows;
        _lu = a.Clone();
        _perm = new int[n];
        for (int i = 0; i < n; i++) _perm[i] = i;
        var tmp = new T[n];
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double max = Scalar.Magnitude(_lu[k, k]);
            for (int i = k + 1; i < n; i++)
            {
                double m = Scalar.Magnitude(_lu[i, k]);
                if (m > max) { max = m; p = i; }
            }
            if (max < 1e-300) throw new SingularMatrixException($"Matrix is singular at column {k}.");
            if (p != k)
            {
                _lu.Row(p).CopyTo(tmp);
                _lu.Row(k).CopyTo(_lu.Row(p));
                tmp.CopyTo(_lu.Row(k));
                (_perm[p], _perm[k]) = (_perm[k], _perm[p]);
            }
            T pivot = _lu[k, k];
            var rk = _lu.Row(k);
            for (int i = k + 1; i < n; i++)
            {
                var ri = _lu.Row(i);
                if (ri[k] == T.Zero) continue;
                T f = ri[k] / pivot;
                ri[k] = f;
                for (int j = k + 1; j < n; j++) ri[j] -= f * rk[j];
            }
        }
    }

    public int Size => _perm.Length;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public T[] Solve(ReadOnlySpan<T> b)
    {
        int n = Size;
        if (b.Length != n) throw new ArgumentException("Dimension mismatch.", nameof(b));
        var x = new T[n];
        for (int i = 0; i < n; i++) x[i] = b[_perm[i]];
        for (int i = 0; i < n; i++)
        {
            var r = _lu.Row(i);
            T s = x[i];
            for (int j = 0; j < i; j++) s -= r[j] * x[j];
            x[i] = s;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            var r = _lu.Row(i);
            T s = x[i];
            for (int j = i + 1; j < n; j++) s -= r[j] * x[j];
            x[i] = s / r[i];
        }
        return x;
    }
}

public sealed class SingularMatrixException(string message) : InvalidOperationException(message);
