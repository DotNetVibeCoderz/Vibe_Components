using GnuRadio.Blocks;

namespace GnuRadio.Fec;

/// <summary>Bit-level adapter so <see cref="ReedSolomon"/> plugs into the <see cref="IFecEncoder"/> plumbing (hard decisions).</summary>
public sealed class ReedSolomonFec(ReedSolomon code) : IFecEncoder, IFecDecoder
{
    private readonly byte[] _bytes = new byte[code.N];

    public ReedSolomon Code { get; } = code;
    public string Name => $"Reed-Solomon ({Code.N},{Code.K})";
    int IFecEncoder.InputSize => Code.K * 8;
    int IFecEncoder.OutputSize => Code.N * 8;
    int IFecDecoder.InputSize => Code.N * 8;
    int IFecDecoder.OutputSize => Code.K * 8;

    public void Encode(ReadOnlySpan<byte> bits, Span<byte> coded)
    {
        var message = Bits.Pack(bits[..(Code.K * 8)]);
        Bits.Unpack(Code.Encode(message), coded);
    }

    public void Decode(ReadOnlySpan<float> llrs, Span<byte> bits)
    {
        Span<byte> hard = stackalloc byte[Code.N * 8];
        for (var i = 0; i < hard.Length; i++) hard[i] = llrs[i] < 0 ? (byte)1 : (byte)0;
        Bits.Pack(hard, _bytes);
        Code.Decode(_bytes);
        Bits.Unpack(_bytes.AsSpan(0, Code.K), bits);
    }
}

/// <summary>Result of a bit-error-rate measurement.</summary>
public readonly record struct BerResult(double EbN0Db, long Bits, long Errors, long Frames, long FrameErrors)
{
    public double Ber => Bits == 0 ? 0 : (double)Errors / Bits;
    public double Fer => Frames == 0 ? 0 : (double)FrameErrors / Frames;
}

/// <summary>
/// Theoretical error rates and Monte-Carlo FEC simulation over BPSK/AWGN — the counterpart of GNU Radio's
/// <c>fec.bercurve_generator</c>.
/// </summary>
public static class BerMath
{
    /// <summary>Gaussian tail Q(x) = ½·erfc(x/√2).</summary>
    public static double Q(double x) => 0.5 * Erfc(x / Math.Sqrt(2));

    /// <summary>Complementary error function (Chebyshev approximation from Numerical Recipes, relative error &lt; 1.2e-7).</summary>
    public static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1 / (1 + 0.5 * z);
        var r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
                t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }

    /// <summary>Uncoded BPSK/QPSK bit error rate.</summary>
    public static double BpskBer(double ebN0Db) => Q(Math.Sqrt(2 * Math.Pow(10, ebN0Db / 10)));

    /// <summary>Approximate Gray-coded M-PSK bit error rate (M ≥ 4).</summary>
    public static double PskBer(int m, double ebN0Db)
    {
        if (m <= 4) return BpskBer(ebN0Db);
        var k = Math.Log2(m);
        var ebn0 = Math.Pow(10, ebN0Db / 10);
        return 2 / k * Q(Math.Sqrt(2 * k * ebn0) * Math.Sin(Math.PI / m));
    }

    /// <summary>Approximate Gray-coded square M-QAM bit error rate.</summary>
    public static double QamBer(int m, double ebN0Db)
    {
        var k = Math.Log2(m);
        var ebn0 = Math.Pow(10, ebN0Db / 10);
        return 4 / k * (1 - 1 / Math.Sqrt(m)) * Q(Math.Sqrt(3 * k * ebn0 / (m - 1)));
    }

    /// <summary>
    /// Noise standard deviation per complex sample for a unit-power signal at the given Eb/N0, with
    /// <paramref name="bitsPerSymbol"/> bits per symbol, <paramref name="samplesPerSymbol"/> samples per symbol and code rate.
    /// Use it as the amplitude of a Gaussian <see cref="NoiseSource{T}"/>.
    /// </summary>
    public static float NoiseVoltage(double ebN0Db, int bitsPerSymbol = 1, int samplesPerSymbol = 1, double codeRate = 1.0)
    {
        var esN0 = Math.Pow(10, ebN0Db / 10) * bitsPerSymbol * codeRate;
        return (float)Math.Sqrt(samplesPerSymbol / esN0);
    }

    /// <summary>
    /// Monte-Carlo BER of a code with BPSK over AWGN: encode random frames, add noise, compute LLRs
    /// 2y/σ², decode, count errors. Runs until <paramref name="maxBits"/> or <paramref name="targetErrors"/>.
    /// </summary>
    public static BerResult Simulate(IFecEncoder encoder, IFecDecoder decoder, double ebN0Db,
        long maxBits = 200_000, long targetErrors = 500, int seed = 1)
    {
        var rng = new FastRandom(seed);
        var k = encoder.InputSize;
        var n = encoder.OutputSize;
        var info = new byte[k];
        var coded = new byte[n];
        var llr = new float[n];
        var decoded = new byte[k];
        var rate = (double)k / n;
        var sigma = Math.Sqrt(1 / (2 * rate * Math.Pow(10, ebN0Db / 10)));
        var scale = (float)(2 / (sigma * sigma));
        long bits = 0, errors = 0, frames = 0, frameErrors = 0;
        while (bits < maxBits && errors < targetErrors)
        {
            rng.NextBits(info);
            encoder.Encode(info, coded);
            for (var i = 0; i < n; i++)
            {
                var y = (coded[i] == 0 ? 1f : -1f) + (float)sigma * rng.NextGaussian();
                llr[i] = scale * y;
            }
            decoder.Decode(llr, decoded);
            var e = Bits.HammingDistance(info, decoded);
            errors += e;
            bits += k;
            frames++;
            if (e > 0) frameErrors++;
        }
        return new BerResult(ebN0Db, bits, errors, frames, frameErrors);
    }
}

/// <summary>
/// Counts bit errors between a reference and a received bit stream (one bit per byte); the running BER is
/// exposed through <see cref="Ber"/>. Inputs: 0 = reference, 1 = received.
/// </summary>
public sealed class BerCounter : Block
{
    private long _bits;
    private long _errors;

    public BerCounter(int skip = 0) : base("ber_counter")
    {
        Skip = skip;
        AddInput<byte>(name: "reference");
        AddInput<byte>(name: "received");
    }

    /// <summary>Initial bits ignored (receiver acquisition time).</summary>
    public int Skip { get; }

    public long BitsCompared => Interlocked.Read(ref _bits);
    public long Errors => Interlocked.Read(ref _errors);
    public double Ber => BitsCompared == 0 ? 0 : (double)Errors / BitsCompared;

    public void Reset()
    {
        Interlocked.Exchange(ref _bits, 0);
        Interlocked.Exchange(ref _errors, 0);
    }

    private long _seen;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.MinInputItems();
        var a = context.Input<byte>(0)[..n];
        var b = context.Input<byte>(1)[..n];
        var start = 0;
        if (_seen < Skip) start = (int)Math.Min(n, Skip - _seen);
        _seen += n;
        var errors = Bits.HammingDistance(a[start..], b[start..]);
        Interlocked.Add(ref _bits, n - start);
        Interlocked.Add(ref _errors, errors);
        context.ConsumeEach(n);
        return WorkResult.Ok;
    }
}
