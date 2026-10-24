using System.Globalization;
using System.Numerics;
using System.Text;

namespace GnuRadio.Fec;

/// <summary>
/// Binary LDPC code from a sparse parity-check matrix (GNU Radio's <c>fec.ldpc_encoder</c>/<c>ldpc_decoder</c>).
/// Encoding uses a systematic form found by Gauss–Jordan elimination; decoding is normalised min-sum belief
/// propagation with early termination when the syndrome is zero. Parity-check matrices can be generated
/// (<see cref="Gallager"/>) or loaded from the alist format GNU Radio ships.
/// </summary>
public sealed class LdpcCode : IFecEncoder, IFecDecoder
{
    private readonly int[][] _checks;     // variable indices of each check
    private readonly int[][] _variables;  // check indices of each variable
    private readonly int[] _infoColumns;
    private readonly int[] _parityColumns;
    private readonly ulong[][] _parityRows; // parity bit r = <row r, info bits> over GF(2)
    private readonly float[] _messages;    // check→variable messages, one per edge
    private readonly int[][] _edgeIndex;   // edge id for (check, position)
    private readonly float[] _posterior;
    private readonly byte[] _hard;
    private readonly float[] _incoming;

    public LdpcCode(int n, IReadOnlyList<int[]> checks, int maxIterations = 50, float normalization = 0.8f)
    {
        N = n;
        M = checks.Count;
        MaxIterations = maxIterations;
        Normalization = normalization;
        _checks = checks.Select(c => c.Distinct().OrderBy(v => v).ToArray()).ToArray();
        var perVariable = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        for (var c = 0; c < M; c++)
            foreach (var v in _checks[c]) perVariable[v].Add(c);
        _variables = perVariable.Select(l => l.ToArray()).ToArray();

        var edge = 0;
        _edgeIndex = new int[M][];
        for (var c = 0; c < M; c++)
        {
            _edgeIndex[c] = new int[_checks[c].Length];
            for (var j = 0; j < _checks[c].Length; j++) _edgeIndex[c][j] = edge++;
        }
        _messages = new float[edge];
        _posterior = new float[n];
        _hard = new byte[n];
        _incoming = new float[_checks.Length == 0 ? 0 : _checks.Max(c => c.Length)];

        (_infoColumns, _parityColumns, _parityRows) = BuildEncoder();
        K = _infoColumns.Length;
    }

    public int N { get; }
    public int M { get; }
    public int K { get; }
    public int MaxIterations { get; set; }
    public float Normalization { get; set; }
    public string Name => $"LDPC ({N},{K})";

    /// <summary>Iterations used by the last decode.</summary>
    public int LastIterations { get; private set; }

    int IFecEncoder.InputSize => K;
    int IFecEncoder.OutputSize => N;
    int IFecDecoder.InputSize => N;
    int IFecDecoder.OutputSize => K;

    /// <summary>
    /// Regular (wc, wr) Gallager code: n columns, n·wc/wr checks, each column in wc checks and each check over
    /// wr columns. Rate ≈ 1 − wc/wr.
    /// </summary>
    public static LdpcCode Gallager(int n, int columnWeight = 3, int rowWeight = 6, int seed = 1)
    {
        if (n % rowWeight != 0) throw new ArgumentException("n must be a multiple of the row weight.", nameof(n));
        var rowsPerBand = n / rowWeight;
        var rng = new FastRandom(seed);
        var checks = new List<int[]>();
        for (var band = 0; band < columnWeight; band++)
        {
            var perm = Enumerable.Range(0, n).ToArray();
            if (band > 0)
                for (var i = n - 1; i > 0; i--)
                {
                    var j = rng.Next(i + 1);
                    (perm[i], perm[j]) = (perm[j], perm[i]);
                }
            for (var r = 0; r < rowsPerBand; r++)
                checks.Add(Enumerable.Range(r * rowWeight, rowWeight).Select(c => perm[c]).ToArray());
        }
        return new LdpcCode(n, checks);
    }

    /// <summary>Parses the alist format (MacKay), as used by GNU Radio's <c>fec/ldpc/*.alist</c> files.</summary>
    public static LdpcCode FromAlist(string text)
    {
        var numbers = text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var p = 0;
        var n = numbers[p++];
        var m = numbers[p++];
        p += 2; // max column / row weight
        var colWeights = numbers.AsSpan(p, n).ToArray();
        p += n;
        var rowWeights = numbers.AsSpan(p, m).ToArray();
        p += m;
        var maxCol = colWeights.Max();
        p += n * maxCol; // column lists (redundant with the rows)
        var maxRow = rowWeights.Max();
        var checks = new List<int[]>();
        for (var r = 0; r < m; r++)
        {
            var row = numbers.AsSpan(p, maxRow).ToArray().Where(v => v > 0).Select(v => v - 1).ToArray();
            p += maxRow;
            checks.Add(row);
        }
        return new LdpcCode(n, checks);
    }

    /// <summary>Writes the parity-check matrix in alist format.</summary>
    public string ToAlist()
    {
        var sb = new StringBuilder();
        var maxCol = _variables.Max(v => v.Length);
        var maxRow = _checks.Max(c => c.Length);
        sb.AppendLine($"{N} {M}");
        sb.AppendLine($"{maxCol} {maxRow}");
        sb.AppendLine(string.Join(' ', _variables.Select(v => v.Length)));
        sb.AppendLine(string.Join(' ', _checks.Select(c => c.Length)));
        foreach (var v in _variables) sb.AppendLine(string.Join(' ', v.Select(c => c + 1).Concat(Enumerable.Repeat(0, maxCol - v.Length))));
        foreach (var c in _checks) sb.AppendLine(string.Join(' ', c.Select(v => v + 1).Concat(Enumerable.Repeat(0, maxRow - c.Length))));
        return sb.ToString();
    }

    private (int[] Info, int[] Parity, ulong[][] Rows) BuildEncoder()
    {
        var words = (N + 63) / 64;
        var h = new ulong[M][];
        for (var r = 0; r < M; r++)
        {
            h[r] = new ulong[words];
            foreach (var v in _checks[r]) h[r][v >> 6] |= 1UL << (v & 63);
        }

        // Gauss–Jordan to reduced row echelon form; pivot columns carry parity.
        var pivots = new List<int>();
        var row = 0;
        for (var col = 0; col < N && row < M; col++)
        {
            var w = col >> 6;
            var bit = 1UL << (col & 63);
            var pivot = -1;
            for (var r = row; r < M; r++)
                if ((h[r][w] & bit) != 0) { pivot = r; break; }
            if (pivot < 0) continue;
            (h[row], h[pivot]) = (h[pivot], h[row]);
            for (var r = 0; r < M; r++)
            {
                if (r == row || (h[r][w] & bit) == 0) continue;
                for (var k = 0; k < words; k++) h[r][k] ^= h[row][k];
            }
            pivots.Add(col);
            row++;
        }
        var parity = pivots.ToArray();
        var isPivot = new bool[N];
        foreach (var c in parity) isPivot[c] = true;
        var info = Enumerable.Range(0, N).Where(c => !isPivot[c]).ToArray();

        // Row r of the RREF says: parity[r] = Σ_j H[r][info j]·u_j.
        var infoWords = (info.Length + 63) / 64;
        var rows = new ulong[parity.Length][];
        for (var r = 0; r < parity.Length; r++)
        {
            rows[r] = new ulong[infoWords];
            for (var j = 0; j < info.Length; j++)
            {
                var c = info[j];
                if ((h[r][c >> 6] & (1UL << (c & 63))) != 0) rows[r][j >> 6] |= 1UL << (j & 63);
            }
        }
        return (info, parity, rows);
    }

    public void Encode(ReadOnlySpan<byte> bits, Span<byte> coded)
    {
        var words = (K + 63) / 64;
        Span<ulong> packed = words <= 128 ? stackalloc ulong[words] : new ulong[words];
        packed.Clear();
        for (var j = 0; j < K; j++)
        {
            var b = bits[j] & 1;
            coded[_infoColumns[j]] = (byte)b;
            if (b != 0) packed[j >> 6] |= 1UL << (j & 63);
        }
        for (var r = 0; r < _parityColumns.Length; r++)
        {
            ulong acc = 0;
            var rowBits = _parityRows[r];
            for (var w = 0; w < words; w++) acc ^= rowBits[w] & packed[w];
            coded[_parityColumns[r]] = (byte)(BitOperations.PopCount(acc) & 1);
        }
    }

    /// <summary>Normalised min-sum decoding; writes the K information bits.</summary>
    public void Decode(ReadOnlySpan<float> llrs, Span<byte> bits)
    {
        Array.Clear(_messages);
        var iterations = 0;
        for (var it = 0; it < MaxIterations; it++)
        {
            iterations = it + 1;
            // Posterior = channel LLR + all incoming check messages.
            for (var v = 0; v < N; v++) _posterior[v] = llrs[v];
            for (var c = 0; c < M; c++)
            {
                var vars = _checks[c];
                var edges = _edgeIndex[c];
                for (var j = 0; j < vars.Length; j++) _posterior[vars[j]] += _messages[edges[j]];
            }
            for (var v = 0; v < N; v++) _hard[v] = _posterior[v] < 0 ? (byte)1 : (byte)0;
            if (SyndromeIsZero()) break;

            // Check update: each outgoing message uses the other incoming variable messages (posterior − own).
            for (var c = 0; c < M; c++)
            {
                var vars = _checks[c];
                var edges = _edgeIndex[c];
                float min1 = float.MaxValue, min2 = float.MaxValue;
                var minIndex = -1;
                var signProduct = 1;
                var incoming = _incoming.AsSpan(0, vars.Length);
                for (var j = 0; j < vars.Length; j++)
                {
                    var q = _posterior[vars[j]] - _messages[edges[j]];
                    incoming[j] = q;
                    var a = MathF.Abs(q);
                    if (q < 0) signProduct = -signProduct;
                    if (a < min1)
                    {
                        min2 = min1;
                        min1 = a;
                        minIndex = j;
                    }
                    else if (a < min2)
                    {
                        min2 = a;
                    }
                }
                for (var j = 0; j < vars.Length; j++)
                {
                    var magnitude = j == minIndex ? min2 : min1;
                    var sign = incoming[j] < 0 ? -signProduct : signProduct;
                    _messages[edges[j]] = Normalization * sign * magnitude;
                }
            }
        }
        LastIterations = iterations;
        for (var j = 0; j < K; j++) bits[j] = _hard[_infoColumns[j]];
    }

    private bool SyndromeIsZero()
    {
        foreach (var check in _checks)
        {
            var parity = 0;
            foreach (var v in check) parity ^= _hard[v];
            if (parity != 0) return false;
        }
        return true;
    }

    /// <summary>True when <paramref name="codeword"/> satisfies every parity check.</summary>
    public bool IsCodeword(ReadOnlySpan<byte> codeword)
    {
        foreach (var check in _checks)
        {
            var parity = 0;
            foreach (var v in check) parity ^= codeword[v] & 1;
            if (parity != 0) return false;
        }
        return true;
    }
}
