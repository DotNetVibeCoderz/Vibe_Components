using GnuRadio.Fft;
using GnuRadio.Filter;
using GnuRadio.Kernels;

namespace GnuRadio.Extensions;

/// <summary>
/// Chirp spread spectrum parameters (the LoRa PHY core, as in <c>gr-lora</c>/<c>gr-lora_sdr</c>).
/// Symbols carry SF bits as cyclic shifts of an up-chirp spanning the bandwidth.
/// </summary>
/// <remarks>
/// This is the CSS modulation and frame synchronisation only: no whitening, Hamming code, interleaving or
/// LoRaWAN header, so it does not interoperate with commercial LoRa radios. It is meant for learning,
/// simulation and custom links.
/// </remarks>
public sealed record LoRaParameters(int SpreadingFactor = 7, double Bandwidth = 125e3, int Oversampling = 4, int PreambleLength = 8)
{
    public int ChipsPerSymbol => 1 << SpreadingFactor;
    public int SamplesPerSymbol => ChipsPerSymbol * Oversampling;
    public double SampleRate => Bandwidth * Oversampling;

    /// <summary>Base up-chirp at the oversampled rate, sweeping −BW/2 → +BW/2.</summary>
    internal Complex32[] UpChirp(int shift = 0)
    {
        var n = SamplesPerSymbol;
        var os = Oversampling;
        var c = new Complex32[n];
        for (var i = 0; i < n; i++)
        {
            var k = (i + shift * os) % n;
            var phase = Math.PI * k * (double)k / (os * (double)os * ChipsPerSymbol) - Math.PI * k / os;
            c[i] = Complex32.FromPolar(1f, (float)phase);
        }
        return c;
    }

    /// <summary>Critically sampled base chirp (one sample per chip) used by the demodulator.</summary>
    internal Complex32[] CriticalUpChirp()
    {
        var n = ChipsPerSymbol;
        var c = new Complex32[n];
        for (var k = 0; k < n; k++) c[k] = Complex32.FromPolar(1f, (float)(Math.PI * k * (double)k / n - Math.PI * k));
        return c;
    }
}

/// <summary>
/// CSS modulator: bytes in (each byte's low SF bits are a symbol value), complex chirps out. Every
/// <c>symbolsPerFrame</c> symbols are preceded by a preamble of up-chirps, two down-chirps, and a length symbol.
/// </summary>
public sealed class LoRaModulator : Block
{
    private readonly LoRaParameters _p;
    private readonly Complex32[][] _symbols;
    private readonly Complex32[] _down;
    private readonly int _symbolsPerFrame;

    public LoRaModulator(LoRaParameters parameters, int symbolsPerFrame = 16) : base("lora_mod")
    {
        _p = parameters;
        _symbolsPerFrame = Math.Min(symbolsPerFrame, parameters.ChipsPerSymbol - 1);
        _symbols = Enumerable.Range(0, parameters.ChipsPerSymbol).Select(parameters.UpChirp).ToArray();
        _down = _symbols[0].Select(c => c.Conjugate()).ToArray();
        RelativeRate = (double)FrameSamples / _symbolsPerFrame;
        AddInput<byte>();
        AddOutput<Complex32>();
    }

    public int FrameSymbols => _p.PreambleLength + 2 + 1 + _symbolsPerFrame;
    public int FrameSamples => FrameSymbols * _p.SamplesPerSymbol;

    protected internal override int MinimumInputItems => _symbolsPerFrame;
    protected internal override int OutputMultiple => FrameSamples;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var frames = Math.Min(context.InputItems(0) / _symbolsPerFrame, context.OutputItems(0) / FrameSamples);
        var input = context.Input<byte>(0);
        var output = context.Output<Complex32>(0);
        var sps = _p.SamplesPerSymbol;
        var mask = _p.ChipsPerSymbol - 1;
        for (var f = 0; f < frames; f++)
        {
            var o = f * FrameSamples;
            context.AddTag(0, context.ItemsWritten(0) + o, "lora_frame", f);
            for (var i = 0; i < _p.PreambleLength; i++, o += sps) _symbols[0].CopyTo(output[o..]);
            for (var i = 0; i < 2; i++, o += sps) _down.CopyTo(output[o..]);
            _symbols[_symbolsPerFrame].CopyTo(output[o..]);
            o += sps;
            for (var s = 0; s < _symbolsPerFrame; s++, o += sps)
                _symbols[input[f * _symbolsPerFrame + s] & mask].CopyTo(output[o..]);
        }
        context.Consume(0, frames * _symbolsPerFrame);
        context.Produce(0, frames * FrameSamples);
        return WorkResult.Ok;
    }
}

/// <summary>
/// CSS demodulator: low-pass and decimate to one sample per chip, dechirp + FFT per symbol, detect the
/// up-chirp preamble (constant peak bin), align on it, find the down-chirps, then read the length symbol
/// and the payload symbols. Output: symbol values as bytes. <see cref="LastPeakBins"/> holds the latest
/// dechirped FFT magnitude for display.
/// </summary>
public sealed class LoRaDemodulator : Block
{
    private readonly LoRaParameters _p;
    private readonly FirFilter<Complex32> _lowpass;
    private readonly Complex32[] _downChirp;
    private readonly Complex32[] _upChirp;
    private readonly FftPlan _fft;
    private readonly Complex32[] _scratch;
    private readonly float[] _magnitude;
    private readonly List<Complex32> _samples = [];
    private readonly Complex32[] _window;
    private Complex32[] _filtered = [];
    private int _decimationPhase;
    private int _consecutive;
    private int _lastBin = -1;
    private State _state;
    private int _remaining;
    private readonly Lock _lock = new();
    private readonly float[] _display;

    private enum State
    {
        Preamble,
        DownChirps,
        Length,
        Payload,
    }

    public LoRaDemodulator(LoRaParameters parameters) : base("lora_demod")
    {
        _p = parameters;
        var n = parameters.ChipsPerSymbol;
        _lowpass = new FirFilter<Complex32>(Firdes.LowPass(1, parameters.SampleRate, parameters.Bandwidth * 0.5, parameters.Bandwidth * 0.15));
        _upChirp = parameters.CriticalUpChirp();
        _downChirp = _upChirp.Select(c => c.Conjugate()).ToArray();
        _fft = new FftPlan(n);
        _scratch = new Complex32[n];
        _magnitude = new float[n];
        _display = new float[n];
        _window = new Complex32[n];
        AddInput<Complex32>();
        AddOutput<byte>();
        TagPropagation = TagPropagationPolicy.None;
    }

    public long FramesDetected { get; private set; }

    /// <summary>Decimation phase (in samples) chosen at the last preamble.</summary>
    public int DecimationPhase => _decimationPhase;

    /// <summary>Latest dechirped spectrum (copy), for plotting the symbol peak.</summary>
    public float[] LastPeakBins
    {
        get
        {
            lock (_lock) return (float[])_display.Clone();
        }
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        var input = context.Input<Complex32>(0)[..n];
        if (_filtered.Length < n) _filtered = new Complex32[n];
        _lowpass.Filter(input, _filtered.AsSpan(0, n));
        _samples.AddRange(_filtered.AsSpan(0, n));
        context.Consume(0, n);

        var output = context.Output<byte>(0);
        var space = context.OutputItems(0);
        var produced = 0;
        var chipsPerSymbol = _p.ChipsPerSymbol;
        var os = _p.Oversampling;
        var symbolSamples = chipsPerSymbol * os;
        var offset = 0;
        while (_samples.Count - offset >= symbolSamples + os && produced < space)
        {
            switch (_state)
            {
                case State.Preamble:
                {
                    // Try every decimation phase and keep the strongest: this absorbs fractional-chip timing offsets.
                    var bin = 0;
                    var best = -1f;
                    var bestPhase = 0;
                    for (var phase = 0; phase < os; phase++)
                    {
                        var b = Dechirp(offset + phase, _downChirp, out var peak);
                        if (peak > best)
                        {
                            best = peak;
                            bin = b;
                            bestPhase = phase;
                        }
                    }
                    var same = bin == _lastBin || Math.Abs(bin - _lastBin) == 1 || Math.Abs(bin - _lastBin) == chipsPerSymbol - 1;
                    _consecutive = same ? _consecutive + 1 : 0;
                    _lastBin = bin;
                    if (_consecutive >= _p.PreambleLength - 3)
                    {
                        // Align: an up-chirp delayed by d chips dechirps to bin (N − d) mod N,
                        // so the next symbol boundary is d chips into this window.
                        _decimationPhase = bestPhase;
                        var shift = bin == 0 ? 0 : chipsPerSymbol - bin;
                        offset += shift * os + bestPhase;
                        _state = State.DownChirps;
                        _consecutive = 0;
                        _remaining = 0;
                        continue;
                    }
                    offset += symbolSamples;
                    break;
                }
                case State.DownChirps:
                {
                    // Still in the preamble while up-chirps dechirp to bin 0; a down-chirp dechirps with the up-chirp.
                    var upBin = Dechirp(offset, _downChirp, out var upPeak);
                    var downBin = Dechirp(offset, _upChirp, out var downPeak);
                    if (downPeak > upPeak && (downBin <= 1 || downBin >= chipsPerSymbol - 1))
                    {
                        if (++_consecutive == 2)
                        {
                            _state = State.Length;
                            FramesDetected++;
                        }
                    }
                    else if ((upBin > 1 && upBin < chipsPerSymbol - 1) || ++_remaining > _p.PreambleLength + 4)
                    {
                        Reset();
                    }
                    offset += symbolSamples;
                    break;
                }
                case State.Length:
                    _remaining = Dechirp(offset, _downChirp, out _);
                    _state = _remaining > 0 ? State.Payload : State.Preamble;
                    offset += symbolSamples;
                    break;
                case State.Payload:
                    output[produced++] = (byte)Dechirp(offset, _downChirp, out _);
                    offset += symbolSamples;
                    if (--_remaining == 0) Reset();
                    break;
            }
        }
        _samples.RemoveRange(0, Math.Min(offset, _samples.Count));
        if (_samples.Count > 64 * symbolSamples) _samples.RemoveRange(0, _samples.Count - 8 * symbolSamples);
        context.Produce(0, produced);
        return WorkResult.Ok;
    }

    private void Reset()
    {
        _state = State.Preamble;
        _consecutive = 0;
        _remaining = 0;
        _lastBin = -1;
    }

    /// <summary>Dechirps the symbol starting <paramref name="start"/> samples into the buffer, one sample per chip.</summary>
    private int Dechirp(int start, Complex32[] reference, out float peak)
    {
        var os = _p.Oversampling;
        var samples = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_samples);
        for (var k = 0; k < _window.Length; k++) _window[k] = samples[start + k * os];
        var window = _window.AsSpan();
        Volk.Multiply(window, reference, _scratch);
        _fft.Execute(_scratch);
        Volk.MagnitudeSquared(_scratch, _magnitude);
        var (index, value) = Volk.ArgMax(_magnitude);
        peak = value;
        lock (_lock) _magnitude.CopyTo(_display, 0);
        return index;
    }
}
