using System.Numerics;

namespace GnuRadio.Fec;

/// <summary>
/// Polar code of length N = 2^n with K information bits (GNU Radio's <c>fec.polar_encoder</c>/<c>polar_decoder_sc</c>).
/// The frozen set is chosen by Bhattacharyya-parameter evolution at a design SNR; decoding is successive
/// cancellation on LLRs.
/// </summary>
public sealed class PolarCode : IFecEncoder, IFecDecoder
{
    private readonly bool[] _frozen;
    private readonly int[] _infoPositions;
    private readonly byte[] _u;
    private readonly float[][] _llrStages;
    private readonly byte[][] _bitStages;

    public PolarCode(int n, int k, double designSnrDb = 0.0)
    {
        if (!BitOperations.IsPow2(n) || n < 2) throw new ArgumentException("N must be a power of two ≥ 2.", nameof(n));
        if (k < 1 || k > n) throw new ArgumentOutOfRangeException(nameof(k));
        N = n;
        K = k;
        DesignSnrDb = designSnrDb;

        // Bhattacharyya evolution. The MSB of a u-index is the first (outermost) polarisation step, applied
        // to the raw channel, so each new step is appended as the LSB: index 2i is the "minus", 2i+1 the "plus" child.
        var z = new[] { Math.Exp(-(double)k / n * Math.Pow(10, designSnrDb / 10)) };
        while (z.Length < n)
        {
            var next = new double[z.Length * 2];
            for (var i = 0; i < z.Length; i++)
            {
                next[2 * i] = 2 * z[i] - z[i] * z[i];
                next[2 * i + 1] = z[i] * z[i];
            }
            z = next;
        }
        _infoPositions = Enumerable.Range(0, n).OrderBy(i => z[i]).Take(k).OrderBy(i => i).ToArray();
        _frozen = Enumerable.Repeat(true, n).ToArray();
        foreach (var i in _infoPositions) _frozen[i] = false;
        _u = new byte[n];

        var levels = BitOperations.Log2((uint)n);
        _llrStages = new float[levels + 1][];
        _bitStages = new byte[levels + 1][];
        for (var l = 0; l <= levels; l++)
        {
            _llrStages[l] = new float[1 << l];
            _bitStages[l] = new byte[1 << l];
        }
    }

    public int N { get; }
    public int K { get; }
    public double DesignSnrDb { get; }
    public string Name => $"Polar ({N},{K})";
    public IReadOnlyList<int> InformationPositions => _infoPositions;

    int IFecEncoder.InputSize => K;
    int IFecEncoder.OutputSize => N;
    int IFecDecoder.InputSize => N;
    int IFecDecoder.OutputSize => K;

    public void Encode(ReadOnlySpan<byte> bits, Span<byte> coded)
    {
        var x = coded[..N];
        x.Clear();
        for (var j = 0; j < K; j++) x[_infoPositions[j]] = (byte)(bits[j] & 1);
        Transform(x);
    }

    /// <summary>x = u·F^⊗n in place, F = [[1,0],[1,1]].</summary>
    private static void Transform(Span<byte> x)
    {
        for (var s = 1; s < x.Length; s <<= 1)
            for (var i = 0; i < x.Length; i += 2 * s)
                for (var j = i; j < i + s; j++)
                    x[j] ^= x[j + s];
    }

    public void Decode(ReadOnlySpan<float> llrs, Span<byte> bits)
    {
        var levels = _llrStages.Length - 1;
        llrs[..N].CopyTo(_llrStages[levels]);
        DecodeNode(levels, 0);
        for (var j = 0; j < K; j++) bits[j] = _u[_infoPositions[j]];
    }

    /// <summary>
    /// Recursive SC on a sub-code of size 2^level starting at u-index <paramref name="offset"/>; LLRs are in
    /// _llrStages[level] and the re-encoded bits are left in _bitStages[level].
    /// </summary>
    private void DecodeNode(int level, int offset)
    {
        var llr = _llrStages[level];
        var size = 1 << level;
        if (level == 0)
        {
            var bit = _frozen[offset] ? (byte)0 : llr[0] < 0 ? (byte)1 : (byte)0;
            _u[offset] = bit;
            _bitStages[0][0] = bit;
            return;
        }
        var half = size / 2;
        var child = _llrStages[level - 1];

        // Upper half: u1 sees x_first ⊕ x_second → f (min-sum check node).
        for (var i = 0; i < half; i++)
        {
            float a = llr[i], b = llr[i + half];
            child[i] = MathF.Sign(a) * MathF.Sign(b) * MathF.Min(MathF.Abs(a), MathF.Abs(b));
        }
        DecodeNode(level - 1, offset);
        Span<byte> left = stackalloc byte[half];
        _bitStages[level - 1].AsSpan(0, half).CopyTo(left);

        // Lower half: u2 seen twice → g (variable node) using the re-encoded upper bits.
        for (var i = 0; i < half; i++)
            child[i] = llr[i + half] + (left[i] == 0 ? llr[i] : -llr[i]);
        DecodeNode(level - 1, offset + half);
        var right = _bitStages[level - 1];

        var outBits = _bitStages[level];
        for (var i = 0; i < half; i++)
        {
            outBits[i] = (byte)(left[i] ^ right[i]);
            outBits[i + half] = right[i];
        }
    }
}
