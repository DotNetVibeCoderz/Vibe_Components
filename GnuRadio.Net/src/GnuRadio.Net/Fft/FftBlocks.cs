using GnuRadio.Filter;
using GnuRadio.Kernels;

namespace GnuRadio.Fft;

/// <summary>
/// FFT of complex vectors (GNU Radio's <c>fft.fft_vcc</c>). Input and output items are vectors of
/// <see cref="Size"/> samples; the window is applied before a forward transform, and <see cref="ShiftOutput"/>
/// moves 0 Hz to the centre.
/// </summary>
public sealed class FftBlock : SyncBlock<Complex32, Complex32>
{
    private readonly FftPlan _plan;
    private readonly float[] _window;

    public FftBlock(int size, bool forward = true, WindowType window = WindowType.BlackmanHarris, bool shift = true)
        : base("fft_vcc", size, size)
    {
        Size = size;
        Forward = forward;
        ShiftOutput = shift;
        _plan = new FftPlan(size, !forward);
        _window = window == WindowType.Rectangular ? [] : Window.Build(window, size);
    }

    public int Size { get; }
    public bool Forward { get; }
    public bool ShiftOutput { get; }

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        for (var v = 0; v < input.Length / Size; v++)
        {
            var src = input.Slice(v * Size, Size);
            var dst = output.Slice(v * Size, Size);
            if (_window.Length > 0 && Forward)
            {
                for (var i = 0; i < Size; i++) dst[i] = src[i] * _window[i];
            }
            else
            {
                src.CopyTo(dst);
            }
            if (!Forward && ShiftOutput) FftMath.Shift(dst);
            _plan.Execute(dst);
            if (Forward && ShiftOutput) FftMath.Shift(dst);
        }
    }
}

/// <summary>
/// Streams complex samples in, emits power spectra in dB as float vectors at a fixed frame rate,
/// with optional exponential averaging (GNU Radio's <c>fft.logpwrfft_c</c> hier block).
/// Samples between frames are skipped, so the cost is set by the frame rate, not the sample rate.
/// </summary>
public sealed class LogPowerFft : Block
{
    private readonly FftPlan _plan;
    private readonly float[] _window;
    private readonly Complex32[] _frame;
    private readonly float[] _power;
    private readonly float[] _average;
    private readonly float _normalization;
    private long _skip;
    private bool _primed;

    public LogPowerFft(double sampleRate, int size, double frameRate = 30, float averageAlpha = 1f,
        WindowType window = WindowType.BlackmanHarris)
        : base("logpwrfft_c")
    {
        SampleRate = sampleRate;
        Size = size;
        FrameRate = frameRate;
        AverageAlpha = averageAlpha;
        _plan = new FftPlan(size);
        _window = Window.Build(window, size);
        _frame = new Complex32[size];
        _power = new float[size];
        _average = new float[size];
        // A complex tone of amplitude 1 reads 0 dB regardless of FFT size and window.
        _normalization = (float)(-20 * Math.Log10(Window.CoherentGain(_window) * size));
        AddInput<Complex32>();
        AddOutput<float>(size);
        TagPropagation = TagPropagationPolicy.None;
    }

    public double SampleRate { get; }
    public int Size { get; }
    public double FrameRate { get; set; }

    /// <summary>1 disables averaging; smaller values average over roughly 1/α frames.</summary>
    public float AverageAlpha { get; set; }

    protected internal override int MinimumInputItems => Size;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var available = context.InputItems(0);
        if (_skip > 0)
        {
            var s = (int)Math.Min(_skip, available);
            context.Consume(0, s);
            _skip -= s;
            return WorkResult.Ok;
        }
        if (available < Size || context.OutputItems(0) < 1) return WorkResult.Ok;

        var input = context.Input<Complex32>(0)[..Size];
        for (var i = 0; i < Size; i++) _frame[i] = input[i] * _window[i];
        _plan.Execute(_frame);
        FftMath.Shift(_frame.AsSpan());
        Volk.MagnitudeSquared(_frame, _power);

        var alpha = AverageAlpha;
        if (!_primed || alpha >= 1f)
        {
            _power.CopyTo(_average, 0);
            _primed = true;
        }
        else
        {
            for (var i = 0; i < Size; i++) _average[i] = alpha * _power[i] + (1 - alpha) * _average[i];
        }
        Volk.PowerToDecibels(_average, context.Output<float>(0)[..Size], _normalization);

        context.Consume(0, Size);
        context.Produce(0, 1);
        _skip = Math.Max(0, (long)(SampleRate / Math.Max(FrameRate, 0.001)) - Size);
        return WorkResult.Ok;
    }
}

/// <summary>Offline spectral estimators over arrays (Welch PSD, spectrogram).</summary>
public static class Spectral
{
    /// <summary>
    /// Welch power spectral density estimate in dB/Hz, 0 Hz centred. Segments of <paramref name="segmentLength"/>
    /// overlap by <paramref name="overlap"/> (0..1) and are averaged.
    /// </summary>
    public static float[] WelchPsd(ReadOnlySpan<Complex32> signal, double sampleRate, int segmentLength = 1024,
        double overlap = 0.5, WindowType window = WindowType.Hann)
    {
        var w = Window.Build(window, segmentLength);
        var u = Window.PowerGain(w);
        var plan = FftPlan.Get(segmentLength);
        var step = Math.Max(1, (int)(segmentLength * (1 - overlap)));
        var acc = new double[segmentLength];
        var frame = new Complex32[segmentLength];
        var count = 0;
        for (var start = 0; start + segmentLength <= signal.Length; start += step)
        {
            for (var i = 0; i < segmentLength; i++) frame[i] = signal[start + i] * w[i];
            plan.Execute(frame);
            for (var i = 0; i < segmentLength; i++) acc[i] += frame[i].MagnitudeSquared;
            count++;
        }
        if (count == 0) throw new ArgumentException("Signal is shorter than one segment.", nameof(signal));
        var psd = new float[segmentLength];
        for (var i = 0; i < segmentLength; i++)
            psd[i] = (float)(10 * Math.Log10(acc[i] / (count * sampleRate * u) + 1e-30));
        FftMath.Shift(psd.AsSpan());
        return psd;
    }

    /// <summary>Spectrogram rows (time) × columns (frequency, 0 Hz centred) of power in dB.</summary>
    public static float[][] Spectrogram(ReadOnlySpan<Complex32> signal, int fftSize = 256, int hop = 128,
        WindowType window = WindowType.Hann)
    {
        var w = Window.Build(window, fftSize);
        var plan = FftPlan.Get(fftSize);
        var frame = new Complex32[fftSize];
        var rows = new List<float[]>();
        for (var start = 0; start + fftSize <= signal.Length; start += hop)
        {
            for (var i = 0; i < fftSize; i++) frame[i] = signal[start + i] * w[i];
            plan.Execute(frame);
            var row = new float[fftSize];
            for (var i = 0; i < fftSize; i++) row[i] = 10f * MathF.Log10(frame[i].MagnitudeSquared + 1e-20f);
            FftMath.Shift(row.AsSpan());
            rows.Add(row);
        }
        return [.. rows];
    }

    /// <summary>Estimates the noise floor of a dB spectrum as a low percentile (robust to strong signals).</summary>
    public static float NoiseFloor(ReadOnlySpan<float> spectrumDb, double percentile = 0.2)
    {
        var sorted = spectrumDb.ToArray();
        Array.Sort(sorted);
        return sorted[(int)Math.Clamp(percentile * (sorted.Length - 1), 0, sorted.Length - 1)];
    }
}
