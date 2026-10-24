using GnuRadio.Analog;
using GnuRadio.Blocks;
using GnuRadio.Filter;
using GnuRadio.Synchronization;

namespace GnuRadio.Digital;

/// <summary>Unpacked bits (0/1) → NRZ levels (−1/+1).</summary>
public sealed class BitsToNrz() : SyncBlock<byte, float>("bits_to_nrz")
{
    protected override void Work(ReadOnlySpan<byte> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = (input[i] & 1) == 0 ? -1f : 1f;
    }
}

/// <summary>
/// Binary FSK / GFSK / GMSK modulator (GNU Radio's <c>digital.gfsk_mod</c>/<c>gmsk_mod</c>): unpacked bits in,
/// complex baseband out. With <c>bt</c> = 0 the frequency pulse is rectangular (plain FSK);
/// with BT &gt; 0 it is Gaussian-shaped. Modulation index h = 2·Δf/R; h = 0.5 with BT 0.3 is GMSK.
/// </summary>
public sealed class FskModulator : HierBlock
{
    public FskModulator(int samplesPerSymbol, double modulationIndex = 1.0, double bt = 0) : base("gfsk_mod")
    {
        DeclareInput<byte>();
        DeclareOutput<Complex32>();
        var nrz = new BitsToNrz();
        var taps = PulseTaps(samplesPerSymbol, bt);
        var shape = new InterpFirFilter<float>(samplesPerSymbol, taps);
        // Peak frequency deviation of h/2 cycles per symbol → π·h/sps radians per sample.
        var mod = new FrequencyModulator((float)(Math.PI * modulationIndex / samplesPerSymbol));
        Connect(nrz, shape, mod);
        ConnectInput(0, nrz);
        ConnectOutput(0, mod);
    }

    /// <summary>Frequency pulse: rectangular of one symbol, convolved with a Gaussian when BT &gt; 0. Peak 1.</summary>
    public static float[] PulseTaps(int samplesPerSymbol, double bt)
    {
        var rect = Enumerable.Repeat(1f, samplesPerSymbol).ToArray();
        if (bt <= 0) return rect;
        var gauss = Firdes.Gaussian(1, samplesPerSymbol, bt, 4 * samplesPerSymbol);
        var taps = new float[rect.Length + gauss.Length - 1];
        for (var i = 0; i < rect.Length; i++)
            for (var j = 0; j < gauss.Length; j++)
                taps[i + j] += rect[i] * gauss[j];
        return taps;
    }
}

/// <summary>
/// Non-coherent binary FSK demodulator (GNU Radio's <c>digital.gfsk_demod</c>): discriminator, matched
/// low-pass, Mueller &amp; Müller clock recovery, slicer. Complex samples in, unpacked bits out.
/// </summary>
public sealed class FskDemodulator : HierBlock
{
    public FskDemodulator(int samplesPerSymbol, double modulationIndex = 1.0, double gainMu = 0.175) : base("gfsk_demod")
    {
        DeclareInput<Complex32>();
        DeclareOutput<byte>();
        var demod = new QuadratureDemod((float)(samplesPerSymbol / (Math.PI * modulationIndex)));
        var smooth = new MovingAverage<float>(samplesPerSymbol, 1f / samplesPerSymbol);
        ClockRecovery = new ClockRecoveryMm(samplesPerSymbol, gainMu: gainMu);
        var slicer = new BinarySlicer();
        Connect(demod, smooth, ClockRecovery, slicer);
        ConnectInput(0, demod);
        ConnectOutput(0, slicer);
    }

    public ClockRecoveryMm ClockRecovery { get; }
}
