namespace Agritech.Core.Data;

/// <summary>Small dense row-major matrix used for regression and simulation (not a general BLAS).</summary>
public sealed class Matrix
{
    private readonly double[] _data;
    public int Rows { get; }
    public int Cols { get; }

    public Matrix(int rows, int cols)
    {
        Rows = rows; Cols = cols; _data = new double[rows * cols];
    }

    public static Matrix FromRows(double[][] rows)
    {
        var m = new Matrix(rows.Length, rows.Length == 0 ? 0 : rows[0].Length);
        for (int i = 0; i < m.Rows; i++) rows[i].CopyTo(m._data.AsSpan(i * m.Cols, m.Cols));
        return m;
    }

    public double this[int r, int c]
    {
        get => _data[r * Cols + c];
        set => _data[r * Cols + c] = value;
    }

    public Span<double> Row(int r) => _data.AsSpan(r * Cols, Cols);

    public Matrix Transpose()
    {
        var t = new Matrix(Cols, Rows);
        for (int r = 0; r < Rows; r++) for (int c = 0; c < Cols; c++) t[c, r] = this[r, c];
        return t;
    }

    public static Matrix operator *(Matrix a, Matrix b)
    {
        if (a.Cols != b.Rows) throw new ArgumentException("Incompatible dimensions.");
        var m = new Matrix(a.Rows, b.Cols);
        for (int i = 0; i < a.Rows; i++)
            for (int k = 0; k < a.Cols; k++)
            {
                double aik = a[i, k];
                if (aik == 0) continue;
                for (int j = 0; j < b.Cols; j++) m[i, j] += aik * b[k, j];
            }
        return m;
    }

    public double[] Multiply(ReadOnlySpan<double> v)
    {
        var res = new double[Rows];
        for (int i = 0; i < Rows; i++)
        {
            double s = 0;
            var row = Row(i);
            for (int j = 0; j < Cols; j++) s += row[j] * v[j];
            res[i] = s;
        }
        return res;
    }

    /// <summary>Solves A·x = b with Gaussian elimination and partial pivoting.</summary>
    public static double[] Solve(Matrix a, ReadOnlySpan<double> b)
    {
        int n = a.Rows;
        if (a.Cols != n || b.Length != n) throw new ArgumentException("Matrix must be square and match b.");
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n] = b[i]; }
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12) throw new InvalidOperationException("Matrix is singular.");
            if (pivot != col) for (int j = col; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (int j = col; j <= n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = m[i, n];
            for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }
}
