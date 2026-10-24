namespace GnuRadio.Fec;

/// <summary>
/// Rate-1/n convolutional code with Viterbi decoding (GNU Radio's <c>fec.cc_encoder</c>/<c>fec.cc_decoder</c>).
/// Frames are terminated with K−1 zero tail bits, so each frame of <c>frameBits</c> information bits becomes
/// (frameBits + K − 1)·n coded bits and the decoder ends in state 0.
/// </summary>
public sealed class ConvolutionalCode : IFecEncoder, IFecDecoder
{
    private readonly int[] _polys;
    private readonly int _states;
    private readonly byte[,] _outputs;
    private readonly float[] _metrics;
    private readonly float[] _next;
    private byte[] _decisions = [];

    /// <param name="frameBits">Information bits per frame.</param>
    /// <param name="constraintLength">K (3–9).</param>
    /// <param name="polynomials">Generator polynomials; bit K−1 is the current input. Default: the CCSDS/NASA K=7 code (171, 133 octal).</param>
    public ConvolutionalCode(int frameBits, int constraintLength = 7, int[]? polynomials = null)
    {
        if (constraintLength is < 2 or > 12) throw new ArgumentOutOfRangeException(nameof(constraintLength));
        FrameBits = frameBits;
        K = constraintLength;
        _polys = polynomials ?? [0b1111001, 0b1011011];
        _states = 1 << (K - 1);
        // _outputs[state, input] = coded bits for that transition, packed (bit j = polynomial j).
        _outputs = new byte[_states, 2];
        for (var s = 0; s < _states; s++)
            for (var u = 0; u < 2; u++)
            {
                var reg = (u << (K - 1)) | s;
                var o = 0;
                for (var j = 0; j < _polys.Length; j++) o |= (int)(uint.PopCount((uint)(reg & _polys[j])) & 1) << j;
                _outputs[s, u] = (byte)o;
            }
        _metrics = new float[_states];
        _next = new float[_states];
    }

    public int FrameBits { get; }
    public int K { get; }
    public int OutputsPerBit => _polys.Length;
    public string Name => $"Convolutional K={K} r=1/{_polys.Length}";

    public int InputSize => FrameBits;
    public int OutputSize => (FrameBits + K - 1) * _polys.Length;
    int IFecDecoder.InputSize => OutputSize;
    int IFecDecoder.OutputSize => FrameBits;

    public void Encode(ReadOnlySpan<byte> bits, Span<byte> coded)
    {
        var state = 0;
        var o = 0;
        var n = _polys.Length;
        for (var i = 0; i < FrameBits + K - 1; i++)
        {
            var u = i < FrameBits ? bits[i] & 1 : 0;
            var outBits = _outputs[state, u];
            for (var j = 0; j < n; j++) coded[o++] = (byte)((outBits >> j) & 1);
            state = ((u << (K - 1)) | state) >> 1;
        }
    }

    /// <summary>Soft-decision Viterbi with correlation metrics; ends in state 0 thanks to the tail.</summary>
    public void Decode(ReadOnlySpan<float> llrs, Span<byte> bits)
    {
        var steps = FrameBits + K - 1;
        var n = _polys.Length;
        if (_decisions.Length < steps * _states) _decisions = new byte[steps * _states];
        Array.Fill(_metrics, float.NegativeInfinity);
        _metrics[0] = 0;
        var mask = _states - 1;
        Span<float> branch = stackalloc float[1 << n];

        for (var t = 0; t < steps; t++)
        {
            // Metric of every possible n-bit output word: Σ (1 − 2c)·llr.
            for (var w = 0; w < branch.Length; w++)
            {
                var m = 0f;
                for (var j = 0; j < n; j++) m += ((w >> j) & 1) == 0 ? llrs[t * n + j] : -llrs[t * n + j];
                branch[w] = m;
            }
            var tail = t >= FrameBits;
            for (var ns = 0; ns < _states; ns++)
            {
                var u = ns >> (K - 2);
                if (tail && u != 0)
                {
                    _next[ns] = float.NegativeInfinity;
                    continue;
                }
                var p0 = (ns << 1) & mask;
                var p1 = p0 | 1;
                var m0 = _metrics[p0] + branch[_outputs[p0, u]];
                var m1 = _metrics[p1] + branch[_outputs[p1, u]];
                if (m1 > m0)
                {
                    _next[ns] = m1;
                    _decisions[t * _states + ns] = 1;
                }
                else
                {
                    _next[ns] = m0;
                    _decisions[t * _states + ns] = 0;
                }
            }
            _next.CopyTo(_metrics, 0);
            // Renormalise to keep the metrics bounded on long frames.
            if ((t & 63) == 63)
            {
                var max = _metrics.Max();
                for (var s = 0; s < _states; s++) _metrics[s] -= max;
            }
        }

        var state = 0;
        for (var t = steps - 1; t >= 0; t--)
        {
            var u = (byte)(state >> (K - 2));
            if (t < FrameBits) bits[t] = u;
            state = ((state << 1) & mask) | _decisions[t * _states + state];
        }
    }
}
