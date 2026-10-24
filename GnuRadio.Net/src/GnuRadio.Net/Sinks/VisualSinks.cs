using System.Runtime.InteropServices;
using GnuRadio.Fft;
using GnuRadio.Filter;
using GnuRadio.Kernels;

namespace GnuRadio.Sinks;

/// <summary>
/// Base of the visualisation sinks (GNU Radio's <c>qtgui</c> sinks without the Qt). They run inside the
/// flowgraph, keep the latest frame behind a lock, and bump <see cref="Version"/> on every new frame. UI
/// components poll at their own frame rate and copy only when the version changed, so rendering never blocks
/// signal processing and no allocation happens per frame.
/// </summary>
public abstract class VisualSink<T> : Block where T : unmanaged
{
    private long _version;
    protected readonly Lock FrameLock = new();

    protected VisualSink(string name, string title, int inputs = 1, int vectorLength = 1) : base(name)
    {
        Title = title;
        for (var i = 0; i < inputs; i++) AddInput<T>(vectorLength);
        TagPropagation = TagPropagationPolicy.None;
    }

    public string Title { get; set; }

    /// <summary>Incremented after each published frame.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Raised on the flowgraph thread after each published frame. Keep handlers short.</summary>
    public event Action? FrameUpdated;

    /// <summary>Frames per second published at most.</summary>
    public double UpdateRate { get; set; } = 30;

    protected void Publish()
    {
        Interlocked.Increment(ref _version);
        FrameUpdated?.Invoke();
    }
}

/// <summary>Type-independent view of a <see cref="TimeSink{T}"/> for UI components.</summary>
public interface ITimeSinkView
{
    string Title { get; }
    int Size { get; }
    double SampleRate { get; }
    int TraceCount { get; }
    TriggerMode Trigger { get; }
    string TraceName(int trace);
    bool TryCopy(ref long version, float[][] destination);
}

/// <summary>When a <see cref="TimeSink{T}"/> captures a frame.</summary>
public enum TriggerMode
{
    /// <summary>Capture continuously.</summary>
    Free,

    /// <summary>Wait for a trigger, but capture anyway after a timeout.</summary>
    Auto,

    /// <summary>Capture only on a trigger.</summary>
    Normal,
}

/// <summary>
/// Oscilloscope: captures <see cref="Size"/> samples from each input (GNU Radio's <c>qtgui.time_sink_f/c</c>),
/// optionally triggered on a rising edge of channel 0. Complex inputs expose real and imaginary traces.
/// </summary>
public sealed class TimeSink<T> : VisualSink<T>, ITimeSinkView where T : unmanaged
{
    private readonly float[][] _capture;
    private readonly float[][] _frame;
    private int _filled;
    private long _skip;
    private bool _armed = true;
    private float _previous;
    private int _waited;

    public TimeSink(int size, double sampleRate, string title = "Time", int inputs = 1) : base("time_sink", title, inputs)
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException("TimeSink supports float and Complex32.");
        Size = size;
        SampleRate = sampleRate;
        var traces = inputs * (IsComplex ? 2 : 1);
        _capture = Enumerable.Range(0, traces).Select(_ => new float[size]).ToArray();
        _frame = Enumerable.Range(0, traces).Select(_ => new float[size]).ToArray();
    }

    public int Size { get; }
    public double SampleRate { get; }
    public bool IsComplex => typeof(T) == typeof(Complex32);

    /// <summary>Number of traces: inputs, doubled for complex.</summary>
    public int TraceCount => _frame.Length;

    public TriggerMode Trigger { get; set; } = TriggerMode.Free;
    public float TriggerLevel { get; set; }

    /// <summary>Trace labels: "Re{0}"/"Im{0}" for complex inputs, "Data 0" for float.</summary>
    public string TraceName(int trace) => IsComplex ? (trace % 2 == 0 ? $"Re{{{trace / 2}}}" : $"Im{{{trace / 2}}}") : $"Data {trace}";

    /// <summary>Copies the latest frame; returns false if nothing changed since <paramref name="version"/>.</summary>
    public bool TryCopy(ref long version, float[][] destination)
    {
        var v = Version;
        if (v == version) return false;
        lock (FrameLock)
            for (var t = 0; t < _frame.Length && t < destination.Length; t++)
                _frame[t].AsSpan(0, Math.Min(Size, destination[t].Length)).CopyTo(destination[t]);
        version = v;
        return true;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.MinInputItems();
        if (n == 0) return WorkResult.Ok;
        var start = 0;
        if (_skip > 0)
        {
            start = (int)Math.Min(_skip, n);
            _skip -= start;
        }
        for (var i = start; i < n && _filled < Size; i++)
        {
            if (_filled == 0 && _armed && Trigger != TriggerMode.Free)
            {
                var v = Sample(context, 0, i).Re;
                var fired = _previous < TriggerLevel && v >= TriggerLevel;
                _previous = v;
                if (!fired && !(Trigger == TriggerMode.Auto && ++_waited > Size * 4)) continue;
                _waited = 0;
            }
            for (var c = 0; c < context.InputCount; c++)
            {
                var (re, im) = Sample(context, c, i);
                if (IsComplex)
                {
                    _capture[2 * c][_filled] = re;
                    _capture[2 * c + 1][_filled] = im;
                }
                else
                {
                    _capture[c][_filled] = re;
                }
            }
            _filled++;
        }
        if (_filled == Size)
        {
            lock (FrameLock)
                for (var t = 0; t < _capture.Length; t++) _capture[t].CopyTo(_frame[t], 0);
            _filled = 0;
            _skip = Math.Max(0, (long)(SampleRate / Math.Max(UpdateRate, 0.01)) - Size);
            Publish();
        }
        context.ConsumeEach(n);
        return WorkResult.Ok;
    }

    private static (float Re, float Im) Sample(WorkContext context, int port, int index)
    {
        if (typeof(T) == typeof(Complex32))
        {
            var c = context.Input<Complex32>(port)[index];
            return (c.Real, c.Imaginary);
        }
        return (context.Input<float>(port)[index], 0);
    }
}

/// <summary>
/// Spectrum analyser (GNU Radio's <c>qtgui.freq_sink_c</c>): windowed FFT power in dB with averaging and
/// max-hold, published at <see cref="VisualSink{T}.UpdateRate"/>. Bin 0 is −fs/2 (0 Hz in the middle).
/// A complex tone of amplitude 1 reads 0 dB.
/// </summary>
public sealed class FrequencySink : VisualSink<Complex32>
{
    private readonly FftPlan _plan;
    private readonly float[] _window;
    private readonly Complex32[] _buffer;
    private readonly float[] _power;
    private readonly float[] _average;
    private readonly float[] _frame;
    private readonly float[] _maxHold;
    private readonly float _offsetDb;
    private int _filled;
    private long _skip;
    private bool _primed;

    public FrequencySink(int fftSize, double sampleRate, double centerFrequency = 0, string title = "Spectrum",
        WindowType window = WindowType.BlackmanHarris) : base("freq_sink", title)
    {
        FftSize = fftSize;
        SampleRate = sampleRate;
        CenterFrequency = centerFrequency;
        _plan = new FftPlan(fftSize);
        _window = Window.Build(window, fftSize);
        _buffer = new Complex32[fftSize];
        _power = new float[fftSize];
        _average = new float[fftSize];
        _frame = new float[fftSize];
        _maxHold = new float[fftSize];
        Array.Fill(_maxHold, -200f);
        _offsetDb = (float)(-20 * Math.Log10(Window.CoherentGain(_window) * fftSize));
    }

    public int FftSize { get; }
    public double SampleRate { get; }
    public double CenterFrequency { get; set; }

    /// <summary>Averaging factor per frame: 1 = none, 0.1 ≈ average of 10 frames.</summary>
    public float Averaging { get; set; } = 0.3f;

    public bool MaxHold { get; set; }

    public void ResetMaxHold()
    {
        lock (FrameLock) Array.Fill(_maxHold, -200f);
    }

    /// <summary>Frequency of bin <paramref name="bin"/> in Hz.</summary>
    public double BinFrequency(int bin) => CenterFrequency + (bin - FftSize / 2) * SampleRate / FftSize;

    /// <summary>Copies the latest spectrum (and max-hold trace when wanted); false if unchanged.</summary>
    public bool TryCopy(ref long version, Span<float> spectrum, Span<float> maxHold = default)
    {
        var v = Version;
        if (v == version) return false;
        lock (FrameLock)
        {
            _frame.AsSpan(0, Math.Min(FftSize, spectrum.Length)).CopyTo(spectrum);
            if (!maxHold.IsEmpty) _maxHold.AsSpan(0, Math.Min(FftSize, maxHold.Length)).CopyTo(maxHold);
        }
        version = v;
        return true;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        var input = context.Input<Complex32>(0);
        var i = 0;
        if (_skip > 0)
        {
            i = (int)Math.Min(_skip, n);
            _skip -= i;
        }
        while (i < n)
        {
            var take = Math.Min(n - i, FftSize - _filled);
            input.Slice(i, take).CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            i += take;
            if (_filled < FftSize) break;
            ComputeFrame();
            _filled = 0;
            _skip = Math.Max(0, (long)(SampleRate / Math.Max(UpdateRate, 0.01)) - FftSize);
            var skipNow = (int)Math.Min(_skip, n - i);
            i += skipNow;
            _skip -= skipNow;
        }
        context.Consume(0, n);
        return WorkResult.Ok;
    }

    private void ComputeFrame()
    {
        for (var k = 0; k < FftSize; k++) _buffer[k] *= _window[k];
        _plan.Execute(_buffer);
        FftMath.Shift(_buffer.AsSpan());
        Volk.MagnitudeSquared(_buffer, _power);
        var a = Averaging;
        if (!_primed || a >= 1)
        {
            _power.CopyTo(_average, 0);
            _primed = true;
        }
        else
        {
            for (var k = 0; k < FftSize; k++) _average[k] = a * _power[k] + (1 - a) * _average[k];
        }
        lock (FrameLock)
        {
            Volk.PowerToDecibels(_average, _frame, _offsetDb);
            if (MaxHold)
                for (var k = 0; k < FftSize; k++) _maxHold[k] = Math.Max(_maxHold[k], _frame[k]);
        }
        Publish();
    }
}

/// <summary>
/// Waterfall (GNU Radio's <c>qtgui.waterfall_sink_c</c>): keeps the last <see cref="History"/> spectra.
/// UIs pull only the rows added since their last read with <see cref="ReadNewRows"/>.
/// </summary>
public sealed class WaterfallSink : VisualSink<Complex32>
{
    private readonly FftPlan _plan;
    private readonly float[] _window;
    private readonly Complex32[] _buffer;
    private readonly float[] _power;
    private readonly float[] _rows;
    private readonly float _offsetDb;
    private long _rowCount;
    private int _filled;
    private long _skip;

    public WaterfallSink(int fftSize, double sampleRate, double centerFrequency = 0, int history = 256, string title = "Waterfall",
        WindowType window = WindowType.BlackmanHarris) : base("waterfall_sink", title)
    {
        FftSize = fftSize;
        SampleRate = sampleRate;
        CenterFrequency = centerFrequency;
        History = history;
        _plan = new FftPlan(fftSize);
        _window = Window.Build(window, fftSize);
        _buffer = new Complex32[fftSize];
        _power = new float[fftSize];
        _rows = new float[fftSize * history];
        _offsetDb = (float)(-20 * Math.Log10(Window.CoherentGain(_window) * fftSize));
        UpdateRate = 25;
    }

    public int FftSize { get; }
    public double SampleRate { get; }
    public double CenterFrequency { get; set; }
    public int History { get; }

    /// <summary>Total rows produced since start.</summary>
    public long RowCount => Interlocked.Read(ref _rowCount);

    /// <summary>
    /// Copies rows newer than <paramref name="cursor"/> (oldest first) into <paramref name="destination"/>,
    /// at most <c>destination.Length / FftSize</c> rows, and advances the cursor. Returns the row count copied.
    /// If the reader fell behind by more than <see cref="History"/>, it skips to the oldest retained row.
    /// </summary>
    public int ReadNewRows(ref long cursor, Span<float> destination)
    {
        lock (FrameLock)
        {
            var total = _rowCount;
            if (cursor < total - History) cursor = total - History;
            var available = (int)(total - cursor);
            var count = Math.Min(available, destination.Length / FftSize);
            for (var r = 0; r < count; r++)
            {
                var slot = (int)((cursor + r) % History);
                _rows.AsSpan(slot * FftSize, FftSize).CopyTo(destination[(r * FftSize)..]);
            }
            cursor += count;
            return count;
        }
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        var input = context.Input<Complex32>(0);
        var i = 0;
        if (_skip > 0)
        {
            i = (int)Math.Min(_skip, n);
            _skip -= i;
        }
        while (i < n)
        {
            var take = Math.Min(n - i, FftSize - _filled);
            input.Slice(i, take).CopyTo(_buffer.AsSpan(_filled));
            _filled += take;
            i += take;
            if (_filled < FftSize) break;
            for (var k = 0; k < FftSize; k++) _buffer[k] *= _window[k];
            _plan.Execute(_buffer);
            FftMath.Shift(_buffer.AsSpan());
            Volk.MagnitudeSquared(_buffer, _power);
            lock (FrameLock)
            {
                var slot = (int)(_rowCount % History);
                Volk.PowerToDecibels(_power, _rows.AsSpan(slot * FftSize, FftSize), _offsetDb);
                _rowCount++;
            }
            Publish();
            _filled = 0;
            _skip = Math.Max(0, (long)(SampleRate / Math.Max(UpdateRate, 0.01)) - FftSize);
            var skipNow = (int)Math.Min(_skip, n - i);
            i += skipNow;
            _skip -= skipNow;
        }
        context.Consume(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>Scatter plot of the latest <see cref="Size"/> complex samples (GNU Radio's <c>qtgui.const_sink_c</c>).</summary>
public sealed class ConstellationSink : VisualSink<Complex32>
{
    private readonly Complex32[] _ring;
    private readonly Complex32[] _frame;
    private int _position;
    private int _sinceFrame;

    public ConstellationSink(int size = 1024, string title = "Constellation") : base("const_sink", title)
    {
        Size = size;
        _ring = new Complex32[size];
        _frame = new Complex32[size];
    }

    public int Size { get; }

    public bool TryCopy(ref long version, Span<Complex32> destination)
    {
        var v = Version;
        if (v == version) return false;
        lock (FrameLock) _frame.AsSpan(0, Math.Min(Size, destination.Length)).CopyTo(destination);
        version = v;
        return true;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        var input = context.Input<Complex32>(0);
        // Only the newest Size samples matter.
        var start = Math.Max(0, n - Size);
        for (var i = start; i < n; i++)
        {
            _ring[_position] = input[i];
            _position = (_position + 1) % Size;
        }
        _sinceFrame += n;
        if (_sinceFrame >= Size / 2)
        {
            _sinceFrame = 0;
            lock (FrameLock)
            {
                _ring.AsSpan(_position).CopyTo(_frame);
                _ring.AsSpan(0, _position).CopyTo(_frame.AsSpan(Size - _position));
            }
            Publish();
        }
        context.Consume(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Shows the latest float vector, for example the output of <see cref="LogPowerFft"/> or a BER curve
/// (GNU Radio's <c>qtgui.vector_sink_f</c>).
/// </summary>
public sealed class VectorFrameSink : VisualSink<float>
{
    private readonly float[] _frame;

    public VectorFrameSink(int vectorLength, string title = "Vector", double xStart = 0, double xStep = 1)
        : base("vector_sink_f", title, 1, vectorLength)
    {
        Length = vectorLength;
        XStart = xStart;
        XStep = xStep;
        _frame = new float[vectorLength];
    }

    public int Length { get; }
    public double XStart { get; }
    public double XStep { get; }

    public bool TryCopy(ref long version, Span<float> destination)
    {
        var v = Version;
        if (v == version) return false;
        lock (FrameLock) _frame.AsSpan(0, Math.Min(Length, destination.Length)).CopyTo(destination);
        version = v;
        return true;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        if (n == 0) return WorkResult.Ok;
        lock (FrameLock) context.Input<float>(0).Slice((n - 1) * Length, Length).CopyTo(_frame);
        context.Consume(0, n);
        Publish();
        return WorkResult.Ok;
    }
}

/// <summary>Running mean of a float stream for meters and number displays (GNU Radio's <c>qtgui.number_sink</c>).</summary>
public sealed class NumberSink : VisualSink<float>
{
    private double _value;
    private long _lastPublish;

    public NumberSink(string title = "Level", float averaging = 0.01f, string unit = "") : base("number_sink", title)
    {
        Averaging = averaging;
        Unit = unit;
    }

    public float Averaging { get; set; }
    public string Unit { get; }
    public double Value => Volatile.Read(ref _value);

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        var v = _value;
        var a = Averaging;
        foreach (var x in context.Input<float>(0)[..n]) v += a * (x - v);
        Volatile.Write(ref _value, v);
        context.Consume(0, n);
        var now = Environment.TickCount64;
        if (now - _lastPublish > 1000 / Math.Max(UpdateRate, 1))
        {
            _lastPublish = now;
            Publish();
        }
        return WorkResult.Ok;
    }
}

/// <summary>Helpers to turn complex sinks' raw spans into interleaved floats for UIs.</summary>
public static class SinkData
{
    public static ReadOnlySpan<float> Interleaved(ReadOnlySpan<Complex32> points) => MemoryMarshal.Cast<Complex32, float>(points);
}
