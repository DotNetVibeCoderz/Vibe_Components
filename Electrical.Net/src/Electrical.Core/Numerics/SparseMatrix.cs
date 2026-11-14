using System.Runtime.CompilerServices;
using System.Numerics;

namespace Electrical.Core.Numerics;

/// <summary>Accumulates (row, col, value) triplets; duplicates are summed when compressed.</summary>
public sealed class SparseBuilder<T>(int rows, int cols) where T : INumberBase<T>
{
    private readonly List<(int Row, int Col, T Value)> _entries = new(rows * 4);

    public int Rows { get; } = rows;
    public int Cols { get; } = cols;
    public int Count => _entries.Count;

    public void Add(int row, int col, T value)
    {
        if ((uint)row >= (uint)Rows || (uint)col >= (uint)Cols)
            throw new ArgumentOutOfRangeException(nameof(row), $"({row},{col}) outside {Rows}x{Cols}.");
        _entries.Add((row, col, value));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public SparseMatrix<T> ToCsr()
    {
        var counts = new int[Rows + 1];
        foreach (var e in _entries) counts[e.Row + 1]++;
        for (int i = 0; i < Rows; i++) counts[i + 1] += counts[i];
        var cols = new int[_entries.Count];
        var vals = new T[_entries.Count];
        var next = (int[])counts.Clone();
        foreach (var e in _entries)
        {
            int p = next[e.Row]++;
            cols[p] = e.Col;
            vals[p] = e.Value;
        }
        // sort each row by column and merge duplicates
        var rowPtr = new int[Rows + 1];
        int w = 0;
        for (int i = 0; i < Rows; i++)
        {
            int s = counts[i], e = counts[i + 1];
            Array.Sort(cols, vals, s, e - s);
            rowPtr[i] = w;
            for (int p = s; p < e; p++)
            {
                if (w > rowPtr[i] && cols[w - 1] == cols[p]) vals[w - 1] += vals[p];
                else { cols[w] = cols[p]; vals[w] = vals[p]; w++; }
            }
        }
        rowPtr[Rows] = w;
        Array.Resize(ref cols, w);
        Array.Resize(ref vals, w);
        return new SparseMatrix<T>(Rows, Cols, rowPtr, cols, vals);
    }
}

/// <summary>Compressed sparse row (CSR) matrix over double or <see cref="Complex"/>.</summary>
public sealed class SparseMatrix<T> where T : INumberBase<T>
{
    internal readonly int[] RowPtr;
    internal readonly int[] ColIdx;
    internal readonly T[] Values;

    public SparseMatrix(int rows, int cols, int[] rowPtr, int[] colIdx, T[] values)
    {
        Rows = rows;
        Cols = cols;
        RowPtr = rowPtr;
        ColIdx = colIdx;
        Values = values;
    }

    public int Rows { get; }
    public int Cols { get; }
    public int NonZeros => RowPtr[Rows];

    public ReadOnlySpan<int> RowIndices(int row) => ColIdx.AsSpan(RowPtr[row], RowPtr[row + 1] - RowPtr[row]);
    public Span<T> RowValues(int row) => Values.AsSpan(RowPtr[row], RowPtr[row + 1] - RowPtr[row]);

    public T this[int row, int col]
    {
        get
        {
            int s = RowPtr[row], e = RowPtr[row + 1];
            int p = Array.BinarySearch(ColIdx, s, e - s, col);
            return p >= 0 ? Values[p] : T.Zero;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public T[] Multiply(ReadOnlySpan<T> x)
    {
        if (x.Length != Cols) throw new ArgumentException("Dimension mismatch.", nameof(x));
        var y = new T[Rows];
        for (int i = 0; i < Rows; i++)
        {
            T s = T.Zero;
            for (int p = RowPtr[i]; p < RowPtr[i + 1]; p++) s += Values[p] * x[ColIdx[p]];
            y[i] = s;
        }
        return y;
    }

    public DenseMatrix<T> ToDense()
    {
        var d = new DenseMatrix<T>(Rows, Cols);
        for (int i = 0; i < Rows; i++)
            for (int p = RowPtr[i]; p < RowPtr[i + 1]; p++) d[i, ColIdx[p]] = Values[p];
        return d;
    }

    public static SparseMatrix<T> FromDense(DenseMatrix<T> d)
    {
        var b = new SparseBuilder<T>(d.Rows, d.Cols);
        for (int i = 0; i < d.Rows; i++)
            for (int j = 0; j < d.Cols; j++)
                if (d[i, j] != T.Zero) b.Add(i, j, d[i, j]);
        return b.ToCsr();
    }

    /// <summary>Solves A·x = b using a fill-reducing sparse LU.</summary>
    public T[] Solve(ReadOnlySpan<T> b) => SparseLU<T>.Factorize(this).Solve(b);

    public override string ToString() => $"SparseMatrix<{typeof(T).Name}>[{Rows}x{Cols}, nnz={NonZeros}]";
}
