using GnuRadio.Blocks;
using GnuRadio.Filter;
using GnuRadio.Synchronization;

namespace GnuRadio.Digital;

/// <summary>Maps symbol values (one per byte) to constellation points (GNU Radio's <c>digital.chunks_to_symbols_bc</c>).</summary>
public sealed class ChunksToSymbols(Constellation constellation) : SyncBlock<byte, Complex32>("chunks_to_symbols_bc")
{
    public Constellation Constellation { get; } = constellation;

    protected override void Work(ReadOnlySpan<byte> input, Span<Complex32> output)
    {
        var mask = Constellation.Arity - 1;
        for (var i = 0; i < input.Length; i++) output[i] = Constellation.Points[input[i] & mask];
    }
}

/// <summary>Hard decisions: complex symbol → symbol value (GNU Radio's <c>digital.constellation_decoder_cb</c>).</summary>
public sealed class ConstellationDecoder(Constellation constellation) : SyncBlock<Complex32, byte>("constellation_decoder_cb")
{
    public Constellation Constellation { get; } = constellation;

    protected override void Work(ReadOnlySpan<Complex32> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = (byte)Constellation.Decide(input[i]);
    }
}

/// <summary>Soft decisions: one LLR per bit, MSB first (GNU Radio's <c>digital.constellation_soft_decoder_cf</c>).</summary>
public sealed class ConstellationSoftDecoder : InterpolatorBlock<Complex32, float>
{
    public ConstellationSoftDecoder(Constellation constellation, float noiseVariance = 0.1f)
        : base("constellation_soft_decoder_cf", constellation.BitsPerSymbol)
    {
        Constellation = constellation;
        NoiseVariance = noiseVariance;
    }

    public Constellation Constellation { get; }
    public float NoiseVariance { get; set; }

    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output)
    {
        var bps = Constellation.BitsPerSymbol;
        for (var i = 0; i < input.Length; i++)
            Constellation.SoftDecision(input[i], NoiseVariance, output.Slice(i * bps, bps));
    }
}

/// <summary>y[n] = (x[n] + y[n−1]) mod M (GNU Radio's <c>digital.diff_encoder_bb</c>).</summary>
public sealed class DiffEncoder(int modulus) : SyncBlock<byte, byte>("diff_encoder_bb")
{
    private int _last;

    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = (byte)(_last = (input[i] + _last) % modulus);
    }
}

/// <summary>y[n] = (x[n] − x[n−1]) mod M (GNU Radio's <c>digital.diff_decoder_bb</c>).</summary>
public sealed class DiffDecoder(int modulus) : SyncBlock<byte, byte>("diff_decoder_bb")
{
    private int _last;

    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = (byte)(((input[i] - _last) % modulus + modulus) % modulus);
            _last = input[i];
        }
    }
}

/// <summary>
/// Fibonacci LFSR (GNU Radio's <c>digital::lfsr</c>): <paramref name="mask"/> selects feedback taps,
/// <paramref name="seed"/> is the initial state, <paramref name="length"/> the register length − 1.
/// </summary>
public sealed class Lfsr(uint mask, uint seed, int length)
{
    private uint _state = seed;

    public uint Mask { get; } = mask;
    public uint Seed { get; } = seed;

    public byte NextBit()
    {
        var output = (byte)(_state & 1);
        var newBit = (byte)(uint.PopCount(_state & Mask) & 1);
        _state = (_state >> 1) | ((uint)newBit << length);
        return output;
    }

    /// <summary>Self-synchronising scramble step: input bit in, scrambled bit out.</summary>
    public byte Scramble(byte input)
    {
        var output = (byte)((uint.PopCount(_state & Mask) & 1) ^ (input & 1));
        _state = (_state >> 1) | ((uint)output << length);
        return output;
    }

    public byte Descramble(byte input)
    {
        var output = (byte)((uint.PopCount(_state & Mask) & 1) ^ (input & 1));
        _state = (_state >> 1) | ((uint)(input & 1) << length);
        return output;
    }

    public void Reset() => _state = Seed;
}

/// <summary>
/// Additive (synchronous) scrambler: XOR with an LFSR sequence, optionally restarting every
/// <c>count</c> bits (GNU Radio's <c>digital.additive_scrambler_bb</c>). Descrambling is the same operation.
/// </summary>
public sealed class AdditiveScrambler : SyncBlock<byte, byte>
{
    private readonly Lfsr _lfsr;
    private readonly int _count;
    private int _bits;

    public AdditiveScrambler(uint mask = 0x8A, uint seed = 0x7F, int length = 7, int count = 0) : base("additive_scrambler_bb")
    {
        _lfsr = new Lfsr(mask, seed, length);
        _count = count;
    }

    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = (byte)(input[i] ^ _lfsr.NextBit());
            if (_count > 0 && ++_bits == _count)
            {
                _bits = 0;
                _lfsr.Reset();
            }
        }
    }
}

/// <summary>Multiplicative (self-synchronising) scrambler (GNU Radio's <c>digital.scrambler_bb</c>).</summary>
public sealed class Scrambler(uint mask = 0x8A, uint seed = 0x7F, int length = 7) : SyncBlock<byte, byte>("scrambler_bb")
{
    private readonly Lfsr _lfsr = new(mask, seed, length);

    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = _lfsr.Scramble(input[i]);
    }
}

/// <summary>Inverse of <see cref="Scrambler"/> (GNU Radio's <c>digital.descrambler_bb</c>).</summary>
public sealed class Descrambler(uint mask = 0x8A, uint seed = 0x7F, int length = 7) : SyncBlock<byte, byte>("descrambler_bb")
{
    private readonly Lfsr _lfsr = new(mask, seed, length);

    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = _lfsr.Descramble(input[i]);
    }
}

/// <summary>
/// Linear modulator for any constellation (GNU Radio's <c>digital.generic_mod</c>): packed bytes in,
/// root-raised-cosine shaped complex baseband out at the given samples per symbol.
/// </summary>
public sealed class ConstellationModulator : HierBlock
{
    public ConstellationModulator(Constellation constellation, int samplesPerSymbol = 4, double excessBandwidth = 0.35,
        bool differential = false, int filterSpan = 11)
        : base("constellation_modulator")
    {
        Constellation = constellation;
        SamplesPerSymbol = samplesPerSymbol;
        DeclareInput<byte>();
        DeclareOutput<Complex32>();

        var repack = new RepackBits(8, constellation.BitsPerSymbol);
        var map = new ChunksToSymbols(constellation);
        var taps = Firdes.RootRaisedCosine(samplesPerSymbol, samplesPerSymbol, 1.0, excessBandwidth, filterSpan * samplesPerSymbol);
        var shape = new InterpFirFilter<Complex32>(samplesPerSymbol, taps);
        if (differential)
        {
            var diff = new DiffEncoder(constellation.Arity);
            Connect(repack, diff, map, shape);
        }
        else
        {
            Connect(repack, map, shape);
        }
        ConnectInput(0, repack);
        ConnectOutput(0, shape);
    }

    public Constellation Constellation { get; }
    public int SamplesPerSymbol { get; }

    /// <summary>Filter delay in symbols introduced by the RRC shaping.</summary>
    public static int DelaySymbols(int filterSpan = 11) => filterSpan / 2;
}

/// <summary>
/// Receiver front end for linear modulations: RRC matched filter, Gardner symbol synchronisation, and
/// carrier recovery (Costas loop for PSK, decision-directed for QAM). Complex samples in, one synchronised
/// symbol per output item. Follow with <see cref="ConstellationDecoder"/> for bits.
/// </summary>
public sealed class ConstellationReceiver : HierBlock
{
    public ConstellationReceiver(Constellation constellation, int samplesPerSymbol = 4, double excessBandwidth = 0.35,
        double timingLoopBandwidth = 0.045, double phaseLoopBandwidth = 2 * Math.PI / 100, int filterSpan = 11)
        : base("constellation_receiver")
    {
        DeclareInput<Complex32>();
        DeclareOutput<Complex32>();
        var taps = Firdes.RootRaisedCosine(1, samplesPerSymbol, 1.0, excessBandwidth, filterSpan * samplesPerSymbol);
        // Front AGC normalises the timing loop gain; the symbol AGC sets the decision scale exactly.
        Agc = new Analog.Agc<Complex32>(1e-3f, 1f);
        var matched = new FirFilterCcf(1, taps);
        SymbolSync = new SymbolSync(samplesPerSymbol, timingLoopBandwidth);
        var meanMagnitude = constellation.Points.Average(p => p.Magnitude);
        SymbolAgc = new Analog.Agc<Complex32>(5e-3f, meanMagnitude);
        Block carrier = constellation.Name is "BPSK" or "QPSK" or "8PSK"
            ? CostasLoop = new CostasLoop(phaseLoopBandwidth, constellation.Name == "BPSK" ? 2 : constellation.Name == "QPSK" ? 4 : 8)
            : DecisionDirected = new DecisionDirectedCarrierRecovery(constellation, phaseLoopBandwidth);
        Connect(Agc, matched, SymbolSync, SymbolAgc, carrier);
        ConnectInput(0, Agc);
        ConnectOutput(0, carrier);
    }

    public Analog.Agc<Complex32> Agc { get; }
    public Analog.Agc<Complex32> SymbolAgc { get; }
    public SymbolSync SymbolSync { get; }
    public CostasLoop? CostasLoop { get; }
    public DecisionDirectedCarrierRecovery? DecisionDirected { get; }
}
