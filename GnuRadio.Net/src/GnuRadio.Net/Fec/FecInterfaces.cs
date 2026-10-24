namespace GnuRadio.Fec;

/// <summary>
/// A block code operating on frames of unpacked bits (GNU Radio's <c>fec::generic_encoder</c>).
/// <see cref="InputSize"/> information bits become <see cref="OutputSize"/> coded bits.
/// </summary>
public interface IFecEncoder
{
    string Name { get; }
    int InputSize { get; }
    int OutputSize { get; }
    double Rate => (double)InputSize / OutputSize;
    void Encode(ReadOnlySpan<byte> bits, Span<byte> coded);
}

/// <summary>
/// Decoder for <see cref="IFecEncoder"/> frames (GNU Radio's <c>fec::generic_decoder</c>). Input is one
/// LLR per coded bit, positive meaning "0"; output is the decoded information bits.
/// </summary>
public interface IFecDecoder
{
    string Name { get; }
    int InputSize { get; }
    int OutputSize { get; }
    void Decode(ReadOnlySpan<float> llrs, Span<byte> bits);
}

/// <summary>Repetition code: each bit sent <c>n</c> times; the decoder sums LLRs. The simplest baseline.</summary>
public sealed class RepetitionCode(int frameBits, int repetitions = 3) : IFecEncoder, IFecDecoder
{
    public string Name => $"Repetition ×{repetitions}";
    int IFecEncoder.InputSize => frameBits;
    int IFecEncoder.OutputSize => frameBits * repetitions;
    int IFecDecoder.InputSize => frameBits * repetitions;
    int IFecDecoder.OutputSize => frameBits;

    public void Encode(ReadOnlySpan<byte> bits, Span<byte> coded)
    {
        for (var i = 0; i < frameBits; i++)
            for (var r = 0; r < repetitions; r++) coded[i * repetitions + r] = bits[i];
    }

    public void Decode(ReadOnlySpan<float> llrs, Span<byte> bits)
    {
        for (var i = 0; i < frameBits; i++)
        {
            var sum = 0f;
            for (var r = 0; r < repetitions; r++) sum += llrs[i * repetitions + r];
            bits[i] = sum < 0 ? (byte)1 : (byte)0;
        }
    }
}

/// <summary>No coding: passes bits through. Lets uncoded links share the FEC plumbing.</summary>
public sealed class UncodedCode(int frameBits) : IFecEncoder, IFecDecoder
{
    public string Name => "Uncoded";
    int IFecEncoder.InputSize => frameBits;
    int IFecEncoder.OutputSize => frameBits;
    int IFecDecoder.InputSize => frameBits;
    int IFecDecoder.OutputSize => frameBits;

    public void Encode(ReadOnlySpan<byte> bits, Span<byte> coded) => bits[..frameBits].CopyTo(coded);

    public void Decode(ReadOnlySpan<float> llrs, Span<byte> bits)
    {
        for (var i = 0; i < frameBits; i++) bits[i] = llrs[i] < 0 ? (byte)1 : (byte)0;
    }
}

/// <summary>Frame-based encoder block: unpacked bits in, coded bits out (GNU Radio's <c>fec.extended_encoder</c>).</summary>
public sealed class FecEncoderBlock : Block
{
    private readonly IFecEncoder _encoder;

    public FecEncoderBlock(IFecEncoder encoder) : base("fec_encoder")
    {
        _encoder = encoder;
        RelativeRate = (double)encoder.OutputSize / encoder.InputSize;
        AddInput<byte>();
        AddOutput<byte>();
    }

    protected internal override int MinimumInputItems => _encoder.InputSize;
    protected internal override int OutputMultiple => _encoder.OutputSize;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var frames = Math.Min(context.InputItems(0) / _encoder.InputSize, context.OutputItems(0) / _encoder.OutputSize);
        var input = context.Input<byte>(0);
        var output = context.Output<byte>(0);
        for (var f = 0; f < frames; f++)
            _encoder.Encode(input.Slice(f * _encoder.InputSize, _encoder.InputSize), output.Slice(f * _encoder.OutputSize, _encoder.OutputSize));
        context.Consume(0, frames * _encoder.InputSize);
        context.Produce(0, frames * _encoder.OutputSize);
        return WorkResult.Ok;
    }
}

/// <summary>Frame-based decoder block: LLRs in, decoded bits out (GNU Radio's <c>fec.extended_decoder</c>).</summary>
public sealed class FecDecoderBlock : Block
{
    private readonly IFecDecoder _decoder;

    public FecDecoderBlock(IFecDecoder decoder) : base("fec_decoder")
    {
        _decoder = decoder;
        RelativeRate = (double)decoder.OutputSize / decoder.InputSize;
        AddInput<float>();
        AddOutput<byte>();
    }

    protected internal override int MinimumInputItems => _decoder.InputSize;
    protected internal override int OutputMultiple => _decoder.OutputSize;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var frames = Math.Min(context.InputItems(0) / _decoder.InputSize, context.OutputItems(0) / _decoder.OutputSize);
        var input = context.Input<float>(0);
        var output = context.Output<byte>(0);
        for (var f = 0; f < frames; f++)
            _decoder.Decode(input.Slice(f * _decoder.InputSize, _decoder.InputSize), output.Slice(f * _decoder.OutputSize, _decoder.OutputSize));
        context.Consume(0, frames * _decoder.InputSize);
        context.Produce(0, frames * _decoder.OutputSize);
        return WorkResult.Ok;
    }
}
