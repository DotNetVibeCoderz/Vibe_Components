namespace GnuRadio.Blocks;

/// <summary>float → int16 with scaling and saturation (GNU Radio's <c>blocks.float_to_short</c>).</summary>
public sealed class FloatToShort(float scale = 32767f) : SyncBlock<float, short>("float_to_short")
{
    protected override void Work(ReadOnlySpan<float> input, Span<short> output)
    {
        for (var i = 0; i < input.Length; i++)
            output[i] = (short)Math.Clamp(MathF.Round(input[i] * scale), short.MinValue, short.MaxValue);
    }
}

/// <summary>int16 → float divided by <c>scale</c> (GNU Radio's <c>blocks.short_to_float</c>).</summary>
public sealed class ShortToFloat(float scale = 32767f) : SyncBlock<short, float>("short_to_float")
{
    protected override void Work(ReadOnlySpan<short> input, Span<float> output)
    {
        var k = 1f / scale;
        for (var i = 0; i < input.Length; i++) output[i] = input[i] * k;
    }
}

/// <summary>int8 → float divided by <c>scale</c> (GNU Radio's <c>blocks.char_to_float</c>).</summary>
public sealed class CharToFloat(float scale = 1f) : SyncBlock<sbyte, float>("char_to_float")
{
    protected override void Work(ReadOnlySpan<sbyte> input, Span<float> output)
    {
        var k = 1f / scale;
        for (var i = 0; i < input.Length; i++) output[i] = input[i] * k;
    }
}

/// <summary>uint8 → float (GNU Radio's <c>blocks.uchar_to_float</c>).</summary>
public sealed class UCharToFloat() : SyncBlock<byte, float>("uchar_to_float")
{
    protected override void Work(ReadOnlySpan<byte> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = input[i];
    }
}

/// <summary>float → uint8 with saturation (GNU Radio's <c>blocks.float_to_uchar</c>).</summary>
public sealed class FloatToUChar() : SyncBlock<float, byte>("float_to_uchar")
{
    protected override void Work(ReadOnlySpan<float> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = (byte)Math.Clamp(MathF.Round(input[i]), 0, 255);
    }
}

/// <summary>Interleaved int16 I/Q pairs → complex (GNU Radio's <c>blocks.interleaved_short_to_complex</c>).</summary>
public sealed class InterleavedShortToComplex(float scale = 32768f) : DecimatorBlock<short, Complex32>("interleaved_short_to_complex", 2)
{
    protected override void Work(ReadOnlySpan<short> input, Span<Complex32> output)
    {
        var k = 1f / scale;
        for (var i = 0; i < output.Length; i++) output[i] = new Complex32(input[2 * i] * k, input[2 * i + 1] * k);
    }
}

/// <summary>Complex → interleaved int16 I/Q pairs (GNU Radio's <c>blocks.complex_to_interleaved_short</c>).</summary>
public sealed class ComplexToInterleavedShort(float scale = 32767f) : InterpolatorBlock<Complex32, short>("complex_to_interleaved_short", 2)
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<short> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            output[2 * i] = (short)Math.Clamp(MathF.Round(input[i].Real * scale), short.MinValue, short.MaxValue);
            output[2 * i + 1] = (short)Math.Clamp(MathF.Round(input[i].Imaginary * scale), short.MinValue, short.MaxValue);
        }
    }
}

/// <summary>Interleaved signed 8-bit I/Q (HackRF, bladeRF SC8) → complex (GNU Radio's <c>blocks.interleaved_char_to_complex</c>).</summary>
public sealed class InterleavedCharToComplex(float scale = 128f) : DecimatorBlock<sbyte, Complex32>("interleaved_char_to_complex", 2)
{
    protected override void Work(ReadOnlySpan<sbyte> input, Span<Complex32> output)
    {
        var k = 1f / scale;
        for (var i = 0; i < output.Length; i++) output[i] = new Complex32(input[2 * i] * k, input[2 * i + 1] * k);
    }
}

/// <summary>Interleaved unsigned 8-bit I/Q, offset 127.5 (RTL-SDR, rtl_tcp) → complex.</summary>
public sealed class InterleavedUCharToComplex() : DecimatorBlock<byte, Complex32>("interleaved_uchar_to_complex", 2)
{
    private static readonly float[] Lut = BuildLut();

    private static float[] BuildLut()
    {
        var lut = new float[256];
        for (var i = 0; i < 256; i++) lut[i] = (i - 127.5f) / 127.5f;
        return lut;
    }

    protected override void Work(ReadOnlySpan<byte> input, Span<Complex32> output)
    {
        for (var i = 0; i < output.Length; i++) output[i] = new Complex32(Lut[input[2 * i]], Lut[input[2 * i + 1]]);
    }
}
