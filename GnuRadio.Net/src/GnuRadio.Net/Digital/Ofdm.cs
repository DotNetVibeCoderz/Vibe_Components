using GnuRadio.Blocks;
using GnuRadio.Fft;

namespace GnuRadio.Digital;

/// <summary>
/// Layout of an OFDM frame. The defaults follow IEEE 802.11a: 64-point FFT, 16-sample cyclic prefix,
/// 48 data carriers on ±1…±26 and pilots on ±7, ±21.
/// </summary>
public sealed record OfdmParameters
{
    public int FftSize { get; init; } = 64;
    public int CyclicPrefix { get; init; } = 16;
    public int[] DataCarriers { get; init; } = DefaultData();
    public int[] PilotCarriers { get; init; } = [-21, -7, 7, 21];
    public Complex32[] PilotSymbols { get; init; } = [new(1, 0), new(1, 0), new(1, 0), new(-1, 0)];
    public Constellation Constellation { get; init; } = Constellation.Qpsk();
    public int SymbolsPerFrame { get; init; } = 10;

    /// <summary>Random-looking but fixed seed for the training symbols, shared by transmitter and receiver.</summary>
    public int TrainingSeed { get; init; } = 0x5EED;

    public int SymbolLength => FftSize + CyclicPrefix;

    /// <summary>Samples in one frame: two training symbols plus the data symbols.</summary>
    public int FrameLength => (2 + SymbolsPerFrame) * SymbolLength;

    public int BitsPerFrame => DataCarriers.Length * Constellation.BitsPerSymbol * SymbolsPerFrame;
    public int BytesPerFrame => BitsPerFrame / 8;

    private static int[] DefaultData()
    {
        var pilots = new HashSet<int> { -21, -7, 7, 21 };
        return Enumerable.Range(-26, 53).Where(k => k != 0 && !pilots.Contains(k)).ToArray();
    }

    internal int Bin(int carrier) => (carrier + FftSize) % FftSize;

    /// <summary>
    /// Schmidl &amp; Cox training symbol: values only on even carriers so the time-domain symbol consists of
    /// two identical halves, and the channel-estimation symbol on every used carrier.
    /// </summary>
    internal (Complex32[] Sync, Complex32[] ChannelEstimate) TrainingSymbols()
    {
        var rng = new FastRandom(TrainingSeed);
        var used = DataCarriers.Concat(PilotCarriers).OrderBy(k => k).ToArray();
        var sync = new Complex32[FftSize];
        var est = new Complex32[FftSize];
        var qpsk = Constellation.Qpsk();
        foreach (var k in used)
        {
            if ((k & 1) == 0) sync[Bin(k)] = qpsk.Points[rng.Next(4)] * MathF.Sqrt(2);
            est[Bin(k)] = qpsk.Points[rng.Next(4)];
        }
        return (sync, est);
    }
}

/// <summary>
/// OFDM transmitter: packed bytes in, complex baseband frames out. Each frame is a Schmidl &amp; Cox
/// synchronisation symbol, a channel-estimation symbol, then <see cref="OfdmParameters.SymbolsPerFrame"/>
/// data symbols carrying pilots, each with a cyclic prefix. A tag <c>ofdm_frame</c> marks every frame start.
/// </summary>
public sealed class OfdmModulator : Block
{
    private readonly OfdmParameters _p;
    private readonly FftPlan _ifft;
    private readonly Complex32[] _syncTime;
    private readonly Complex32[] _estTime;
    private readonly Complex32[] _symbol;
    private readonly byte[] _bits;
    private readonly float _scale;

    public OfdmModulator(OfdmParameters? parameters = null) : base("ofdm_tx")
    {
        _p = parameters ?? new OfdmParameters();
        if (_p.BitsPerFrame % 8 != 0) throw new ArgumentException("Bits per frame must be a whole number of bytes.");
        _ifft = new FftPlan(_p.FftSize, inverse: true);
        // The IFFT is unnormalised: U unit-power carriers give sample power U, so scale by 1/√U.
        _scale = 1f / MathF.Sqrt(_p.DataCarriers.Length + _p.PilotCarriers.Length);
        var (sync, est) = _p.TrainingSymbols();
        _syncTime = Ifft(sync);
        _estTime = Ifft(est);
        _symbol = new Complex32[_p.FftSize];
        _bits = new byte[_p.BitsPerFrame];
        RelativeRate = (double)_p.FrameLength / _p.BytesPerFrame;
        AddInput<byte>();
        AddOutput<Complex32>();
    }

    public OfdmParameters Parameters => _p;

    protected internal override int MinimumInputItems => _p.BytesPerFrame;
    protected internal override int OutputMultiple => _p.FrameLength;

    private Complex32[] Ifft(Complex32[] freq)
    {
        var t = (Complex32[])freq.Clone();
        _ifft.Execute(t);
        for (var i = 0; i < t.Length; i++) t[i] *= _scale;
        return t;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var frames = Math.Min(context.InputItems(0) / _p.BytesPerFrame, context.OutputItems(0) / _p.FrameLength);
        if (frames == 0) return WorkResult.Ok;
        var input = context.Input<byte>(0);
        var output = context.Output<Complex32>(0);
        var n = _p.FftSize;
        var cp = _p.CyclicPrefix;
        var bps = _p.Constellation.BitsPerSymbol;
        for (var f = 0; f < frames; f++)
        {
            var frame = output.Slice(f * _p.FrameLength, _p.FrameLength);
            context.AddTag(0, context.ItemsWritten(0) + f * _p.FrameLength, "ofdm_frame", f);
            WriteSymbol(frame, 0, _syncTime);
            WriteSymbol(frame, 1, _estTime);
            Bits.Unpack(input.Slice(f * _p.BytesPerFrame, _p.BytesPerFrame), _bits);
            var bit = 0;
            for (var s = 0; s < _p.SymbolsPerFrame; s++)
            {
                Array.Clear(_symbol);
                foreach (var k in _p.DataCarriers)
                {
                    var value = 0;
                    for (var b = 0; b < bps; b++) value = (value << 1) | _bits[bit++];
                    _symbol[_p.Bin(k)] = _p.Constellation.Points[value];
                }
                for (var i = 0; i < _p.PilotCarriers.Length; i++)
                    _symbol[_p.Bin(_p.PilotCarriers[i])] = _p.PilotSymbols[i];
                _ifft.Execute(_symbol);
                for (var i = 0; i < n; i++) _symbol[i] *= _scale;
                WriteSymbol(frame, 2 + s, _symbol);
            }
        }
        context.Consume(0, frames * _p.BytesPerFrame);
        context.Produce(0, frames * _p.FrameLength);

        void WriteSymbol(Span<Complex32> frame, int index, ReadOnlySpan<Complex32> time)
        {
            var dst = frame.Slice(index * (n + cp), n + cp);
            time[(n - cp)..].CopyTo(dst);
            time.CopyTo(dst[cp..]);
        }
        return WorkResult.Ok;
    }
}

/// <summary>
/// OFDM receiver matching <see cref="OfdmModulator"/>: Schmidl &amp; Cox timing and fractional frequency
/// offset estimation, least-squares channel estimation from the training symbol, pilot-based common phase
/// tracking, and demapping. Output 0 is packed bytes; optional output 1 is the equalised data-carrier symbols.
/// </summary>
public sealed class OfdmDemodulator : Block
{
    private readonly OfdmParameters _p;
    private readonly FftPlan _fft;
    private readonly Complex32[] _estReference;
    private readonly Complex32[] _channel;
    private readonly Complex32[] _symbol;
    private readonly byte[] _bits;
    private readonly Complex32[] _buffer;
    private int _count;
    private readonly int _half;

    public OfdmDemodulator(OfdmParameters? parameters = null, float threshold = 0.7f) : base("ofdm_rx")
    {
        _p = parameters ?? new OfdmParameters();
        _fft = new FftPlan(_p.FftSize);
        (_, _estReference) = _p.TrainingSymbols();
        _channel = new Complex32[_p.FftSize];
        _symbol = new Complex32[_p.FftSize];
        _bits = new byte[_p.BitsPerFrame];
        _half = _p.FftSize / 2;
        _buffer = new Complex32[Math.Max(1 << 14, 4 * _p.FrameLength)];
        Threshold = threshold;
        AddInput<Complex32>();
        AddOutput<byte>();
        AddOutput<Complex32>(name: "symbols", optional: true);
        TagPropagation = TagPropagationPolicy.None;
    }

    public float Threshold { get; set; }
    public long FramesDecoded { get; private set; }

    /// <summary>Last estimated carrier offset in subcarrier spacings.</summary>
    public double LastFrequencyOffset { get; private set; }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var symbolsPerFrame = _p.DataCarriers.Length * _p.SymbolsPerFrame;
        var produced = 0;
        var producedSymbols = 0;
        var outBytes = context.Output<byte>(0);
        var outSymbols = context.Output<Complex32>(1);

        // Pull input into the private search buffer (bounded so a stalled consumer cannot grow it forever).
        var take = Math.Min(context.InputItems(0), _buffer.Length - _count);
        if (take > 0)
        {
            context.Input<Complex32>(0)[..take].CopyTo(_buffer.AsSpan(_count));
            _count += take;
            context.Consume(0, take);
        }

        while (context.OutputItems(0) - produced >= _p.BytesPerFrame && context.OutputItems(1) - producedSymbols >= symbolsPerFrame)
        {
            var start = FindFrame(out var cfo, out var searchedUpTo);
            if (start < 0)
            {
                Discard(searchedUpTo);
                break;
            }
            if (start + _p.FrameLength + _half > _count)
            {
                // Keep the whole plateau: discarding up to its middle would bias the next timing estimate.
                Discard(start - _p.CyclicPrefix);
                break;
            }
            LastFrequencyOffset = cfo;
            DemodulateFrame(start, cfo, outBytes.Slice(produced, _p.BytesPerFrame), outSymbols.Slice(producedSymbols, symbolsPerFrame));
            produced += _p.BytesPerFrame;
            producedSymbols += symbolsPerFrame;
            FramesDecoded++;
            Discard(start + _p.FrameLength - _p.SymbolLength);
        }
        context.Produce(0, produced);
        context.Produce(1, producedSymbols);
        return WorkResult.Ok;
    }

    private void Discard(int samples)
    {
        samples = Math.Clamp(samples, 0, _count);
        _buffer.AsSpan(samples, _count - samples).CopyTo(_buffer);
        _count -= samples;
    }

    /// <summary>
    /// Schmidl &amp; Cox metric M(d) = |P(d)|² / R(d)². Returns the middle of the plateau (inside the cyclic
    /// prefix of the sync symbol), or −1. <paramref name="searchedUpTo"/> is how far the buffer can be dropped.
    /// </summary>
    private int FindFrame(out double cfo, out int searchedUpTo)
    {
        cfo = 0;
        var l = _half;
        var r = _buffer.AsSpan(0, _count);
        searchedUpTo = Math.Max(0, _count - 2 * l - _p.SymbolLength);
        if (_count < 2 * l + 1) return -1;

        Complex32 p = default;
        float e = 0;
        for (var m = 0; m < l; m++)
        {
            p += r[m].Conjugate() * r[m + l];
            e += r[m + l].MagnitudeSquared;
        }
        var first = -1;
        var last = -1;
        var best = 0f;
        Complex32 bestP = default;
        var closed = false;
        for (var d = 0; d + 2 * l < r.Length; d++)
        {
            var metric = p.MagnitudeSquared / Math.Max(e * e, 1e-20f);
            if (metric > Threshold)
            {
                if (first < 0) first = d;
                last = d;
                if (metric > best)
                {
                    best = metric;
                    bestP = p;
                }
            }
            else if (first >= 0 && d - last > _p.CyclicPrefix)
            {
                closed = true;
                break;
            }
            // Slide the window by one sample.
            p += r[d + l].Conjugate() * r[d + 2 * l] - r[d].Conjugate() * r[d + l];
            e += r[d + 2 * l].MagnitudeSquared - r[d + l].MagnitudeSquared;
        }
        if (first < 0) return -1;
        if (!closed)
        {
            // The plateau runs into the end of the buffer: its middle is not known yet, so wait for more samples.
            searchedUpTo = Math.Max(0, first - 1);
            return -1;
        }
        // Plateau spans the cyclic prefix; its middle is safely inside the CP.
        cfo = bestP.Phase / Math.PI;
        return (first + last) / 2;
    }

    private void DemodulateFrame(int start, double cfo, Span<byte> bytes, Span<Complex32> symbolsOut)
    {
        var n = _p.FftSize;
        var len = _p.SymbolLength;
        var w = -2 * Math.PI * cfo / n;

        void LoadSymbol(int index)
        {
            var offset = start + index * len;
            for (var i = 0; i < n; i++)
            {
                var t = offset + i - start;
                _symbol[i] = _buffer[offset + i] * Complex32.FromPolar(1f, (float)(w * t));
            }
            _fft.Execute(_symbol);
        }

        // Channel estimate from the second training symbol.
        LoadSymbol(1);
        foreach (var k in _p.DataCarriers.Concat(_p.PilotCarriers))
        {
            var b = _p.Bin(k);
            _channel[b] = _symbol[b] / _estReference[b];
        }

        var bps = _p.Constellation.BitsPerSymbol;
        var bit = 0;
        var outIndex = 0;
        for (var s = 0; s < _p.SymbolsPerFrame; s++)
        {
            LoadSymbol(2 + s);
            // Common phase error from the pilots.
            Complex32 acc = default;
            for (var i = 0; i < _p.PilotCarriers.Length; i++)
            {
                var b = _p.Bin(_p.PilotCarriers[i]);
                acc += _symbol[b] / _channel[b] * _p.PilotSymbols[i].Conjugate();
            }
            var cpe = Complex32.FromPolar(1f, -acc.Phase);
            foreach (var k in _p.DataCarriers)
            {
                var b = _p.Bin(k);
                var z = _symbol[b] / _channel[b] * cpe;
                symbolsOut[outIndex++] = z;
                var v = _p.Constellation.Decide(z);
                for (var j = bps - 1; j >= 0; j--) _bits[bit++] = (byte)((v >> j) & 1);
            }
        }
        Bits.Pack(_bits, bytes);
    }
}
