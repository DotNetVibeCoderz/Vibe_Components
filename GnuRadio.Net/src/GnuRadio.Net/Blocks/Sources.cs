using System.Runtime.InteropServices;

namespace GnuRadio.Blocks;

/// <summary>Waveform of a <see cref="SignalSource{T}"/>.</summary>
public enum Waveform
{
    Constant,
    Sine,
    Cosine,
    Square,
    Triangle,
    Sawtooth,
}

internal static class ItemTypes
{
    public static void RequireFloatOrComplex<T>(string block)
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException($"{block} supports float and Complex32 items, not {typeof(T).Name}.");
    }

    public static bool IsComplex<T>() => typeof(T) == typeof(Complex32);
}

/// <summary>
/// Periodic signal generator (GNU Radio's <c>analog.sig_source_f/c</c>). For complex output the
/// sine/cosine waveforms produce the analytic tone cos + j·sin. Frequency, amplitude, offset and
/// phase can be changed while running, directly or through the <c>cmd</c> message port
/// (dict with keys <c>freq</c>, <c>ampl</c>, <c>offset</c>, <c>phase</c>).
/// </summary>
public sealed class SignalSource<T> : SourceBlock<T> where T : unmanaged
{
    private double _phase;
    private volatile float _frequency;
    private volatile float _amplitude;
    private volatile float _offset;

    public SignalSource(double sampleRate, Waveform waveform, double frequency, float amplitude = 1f, float offset = 0f, double phase = 0)
        : base("sig_source")
    {
        ItemTypes.RequireFloatOrComplex<T>("SignalSource");
        SampleRate = sampleRate;
        Waveform = waveform;
        _frequency = (float)frequency;
        _amplitude = amplitude;
        _offset = offset;
        _phase = phase;
        RegisterMessageInput("cmd", OnCommand);
    }

    public double SampleRate { get; }
    public Waveform Waveform { get; set; }

    public double Frequency
    {
        get => _frequency;
        set => _frequency = (float)value;
    }

    public float Amplitude
    {
        get => _amplitude;
        set => _amplitude = value;
    }

    public float Offset
    {
        get => _offset;
        set => _offset = value;
    }

    private void OnCommand(Pmt msg)
    {
        var d = msg.AsDict();
        if (d.TryGetValue("freq", out var f)) Frequency = f.ToDouble();
        if (d.TryGetValue("ampl", out var a)) Amplitude = (float)a.ToDouble();
        if (d.TryGetValue("offset", out var o)) Offset = (float)o.ToDouble();
        if (d.TryGetValue("phase", out var p)) _phase = p.ToDouble();
    }

    protected override int Generate(Span<T> output)
    {
        var inc = 2 * Math.PI * _frequency / SampleRate;
        var ampl = _amplitude;
        var offset = _offset;
        var phase = _phase;
        var n = output.Length;

        if (typeof(T) == typeof(Complex32))
        {
            var c = MemoryMarshal.Cast<T, Complex32>(output);
            if (Waveform is Waveform.Sine or Waveform.Cosine)
            {
                // Recursive phasor with periodic exact resync: one complex multiply per sample.
                var rot = Complex32.FromPolar(1f, (float)inc);
                var p = Complex32.One;
                for (var i = 0; i < n; i++)
                {
                    if ((i & 255) == 0) p = Complex32.FromPolar(1f, (float)WrapPhase(phase + inc * i));
                    var v = p * ampl;
                    c[i] = new Complex32(v.Real + offset, v.Imaginary);
                    p *= rot;
                }
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    var v = Shape(WrapPhase(phase + inc * i), Waveform);
                    var q = Shape(WrapPhase(phase + inc * i - Math.PI / 2), Waveform);
                    c[i] = new Complex32(ampl * v + offset, ampl * q);
                }
            }
        }
        else
        {
            var f = MemoryMarshal.Cast<T, float>(output);
            for (var i = 0; i < n; i++)
                f[i] = ampl * Shape(WrapPhase(phase + inc * i), Waveform) + offset;
        }
        _phase = WrapPhase(phase + inc * n);
        return n;
    }

    private static double WrapPhase(double p)
    {
        p %= 2 * Math.PI;
        return p < 0 ? p + 2 * Math.PI : p;
    }

    /// <summary>Real waveform value at phase <paramref name="p"/> in [0, 2π).</summary>
    private static float Shape(double p, Waveform w) => w switch
    {
        Waveform.Constant => 1f,
        Waveform.Sine => (float)Math.Sin(p),
        Waveform.Cosine => (float)Math.Cos(p),
        Waveform.Square => p < Math.PI ? 1f : 0f,
        Waveform.Triangle => (float)(p < Math.PI ? p / Math.PI : 2 - p / Math.PI),
        Waveform.Sawtooth => (float)(p / (2 * Math.PI)),
        _ => 0f,
    };
}

/// <summary>Noise distribution of a <see cref="NoiseSource{T}"/>.</summary>
public enum NoiseType
{
    Uniform,
    Gaussian,
    Laplacian,
    Impulse,
}

/// <summary>
/// Random noise generator (GNU Radio's <c>analog.noise_source_f/c</c>). <see cref="Amplitude"/> is the
/// standard deviation for Gaussian noise; for complex output the power is split evenly between I and Q.
/// </summary>
public sealed class NoiseSource<T> : SourceBlock<T> where T : unmanaged
{
    private readonly FastRandom _random;
    private volatile float _amplitude;

    public NoiseSource(NoiseType type, float amplitude, long seed = 0) : base("noise_source")
    {
        ItemTypes.RequireFloatOrComplex<T>("NoiseSource");
        Type = type;
        _amplitude = amplitude;
        _random = new FastRandom(seed);
    }

    public NoiseType Type { get; set; }

    public float Amplitude
    {
        get => _amplitude;
        set => _amplitude = value;
    }

    protected override int Generate(Span<T> output)
    {
        var a = _amplitude;
        if (typeof(T) == typeof(Complex32))
        {
            var c = MemoryMarshal.Cast<T, Complex32>(output);
            for (var i = 0; i < c.Length; i++)
                c[i] = Type == NoiseType.Gaussian ? _random.NextComplexGaussian() * a : new Complex32(Draw(a * 0.70710678f), Draw(a * 0.70710678f));
        }
        else
        {
            var f = MemoryMarshal.Cast<T, float>(output);
            for (var i = 0; i < f.Length; i++) f[i] = Draw(a);
        }
        return output.Length;
    }

    private float Draw(float a) => Type switch
    {
        NoiseType.Gaussian => a * _random.NextGaussian(),
        NoiseType.Uniform => a * (2f * _random.NextSingle() - 1f),
        NoiseType.Laplacian => a * _random.NextLaplacian(),
        NoiseType.Impulse => _random.NextSingle() < 0.001f ? a * _random.NextGaussian() * 10f : 0f,
        _ => 0f,
    };
}

/// <summary>Produces the same value forever (GNU Radio's <c>analog.const_source</c>).</summary>
public sealed class ConstantSource<T>(T value) : SourceBlock<T>("const_source") where T : unmanaged
{
    public T Value { get; set; } = value;

    protected override int Generate(Span<T> output)
    {
        output.Fill(Value);
        return output.Length;
    }
}

/// <summary>Produces zeros forever (GNU Radio's <c>blocks.null_source</c>).</summary>
public sealed class NullSource<T>(int vectorLength = 1) : SourceBlock<T>("null_source", vectorLength) where T : unmanaged
{
    protected override int Generate(Span<T> output)
    {
        output.Clear();
        return output.Length / VectorLength;
    }
}

/// <summary>
/// Plays back an array, optionally repeating, with optional stream tags (GNU Radio's <c>blocks.vector_source</c>).
/// </summary>
public sealed class VectorSource<T> : Block where T : unmanaged
{
    private readonly T[] _data;
    private readonly Tag[] _tags;
    private readonly int _vlen;
    private int _position;
    private long _pass;

    public VectorSource(ReadOnlySpan<T> data, bool repeat = false, int vectorLength = 1, IEnumerable<Tag>? tags = null)
        : base("vector_source")
    {
        if (data.Length % vectorLength != 0)
            throw new ArgumentException("Data length must be a multiple of the vector length.", nameof(data));
        _data = data.ToArray();
        _vlen = vectorLength;
        _tags = tags?.ToArray() ?? [];
        Repeat = repeat;
        AddOutput<T>(vectorLength);
    }

    public bool Repeat { get; }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var totalItems = _data.Length / _vlen;
        if (totalItems == 0) return WorkResult.Done;
        var output = context.Output<T>(0);
        var space = context.OutputItems(0);
        var written = 0;
        while (written < space)
        {
            if (_position == totalItems)
            {
                if (!Repeat) break;
                _position = 0;
                _pass++;
            }
            var n = Math.Min(space - written, totalItems - _position);
            var baseOffset = context.ItemsWritten(0) + written;
            foreach (var t in _tags)
                if (t.Offset >= _position && t.Offset < _position + n)
                    context.AddTag(0, baseOffset + t.Offset - _position, t.Key, t.Value);
            _data.AsSpan(_position * _vlen, n * _vlen).CopyTo(output[(written * _vlen)..]);
            _position += n;
            written += n;
        }
        context.Produce(0, written);
        return !Repeat && _position == totalItems ? WorkResult.Done : WorkResult.Ok;
    }
}
