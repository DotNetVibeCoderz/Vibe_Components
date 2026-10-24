using GnuRadio.Digital;
using GnuRadio.Extensions;

namespace GnuRadio.Gallery;

/// <summary>Running error-vector magnitude of synchronised symbols against a constellation.</summary>
public sealed class EvmProbe(Constellation constellation) : SinkBlock<Complex32>("evm_probe")
{
    private double _error = 1;
    private double _power = 1;

    public Constellation Constellation { get; } = constellation;

    /// <summary>RMS EVM as a fraction (0.05 = 5 %).</summary>
    public double Evm => Math.Sqrt(Volatile.Read(ref _error) / Math.Max(Volatile.Read(ref _power), 1e-12));

    /// <summary>EVM expressed as a modulation error ratio in dB.</summary>
    public double MerDb => -20 * Math.Log10(Math.Max(Evm, 1e-6));

    protected override bool Consume(ReadOnlySpan<Complex32> input)
    {
        double e = _error, p = _power;
        foreach (var s in input)
        {
            var d = Constellation.Points[Constellation.Decide(s)];
            e = 0.999 * e + 0.001 * (s - d).MagnitudeSquared;
            p = 0.999 * p + 0.001 * d.MagnitudeSquared;
        }
        Volatile.Write(ref _error, e);
        Volatile.Write(ref _power, p);
        return true;
    }
}

/// <summary>Compares fixed-size frames (bytes) against a reference payload and counts bit errors.</summary>
public sealed class FrameBerProbe(byte[] reference) : SinkBlock<byte>("frame_ber_probe")
{
    private readonly List<byte> _pending = [];
    private long _bits;
    private long _errors;

    public long Frames { get; private set; }
    public double Ber => Interlocked.Read(ref _bits) == 0 ? 0 : (double)Interlocked.Read(ref _errors) / Interlocked.Read(ref _bits);

    protected override bool Consume(ReadOnlySpan<byte> input)
    {
        _pending.AddRange(input);
        while (_pending.Count >= reference.Length)
        {
            var errors = 0;
            for (var i = 0; i < reference.Length; i++) errors += (int)uint.PopCount((uint)(_pending[i] ^ reference[i]));
            _pending.RemoveRange(0, reference.Length);
            Interlocked.Add(ref _errors, errors);
            Interlocked.Add(ref _bits, reference.Length * 8L);
            Frames++;
        }
        return true;
    }
}

/// <summary>Collects blocks of symbols and classifies them with a <see cref="ModulationClassifier"/>.</summary>
public sealed class ClassifierProbe(ModulationClassifier classifier, int blockSize = 4096) : SinkBlock<Complex32>("classifier_probe")
{
    private readonly Complex32[] _block = new Complex32[blockSize];
    private int _filled;
    private readonly Lock _lock = new();
    private (string Label, IReadOnlyDictionary<string, double> Scores) _latest = ("—", new Dictionary<string, double>());

    public (string Label, IReadOnlyDictionary<string, double> Scores) Latest
    {
        get
        {
            lock (_lock) return _latest;
        }
    }

    protected override bool Consume(ReadOnlySpan<Complex32> input)
    {
        foreach (var s in input)
        {
            _block[_filled++] = s;
            if (_filled < _block.Length) continue;
            _filled = 0;
            var result = classifier.Classify(_block);
            lock (_lock) _latest = result;
        }
        return true;
    }
}

/// <summary>Remembers the last decoded symbols and checks frames against an expected payload.</summary>
public sealed class SymbolFrameProbe(byte[] payload) : SinkBlock<byte>("symbol_frame_probe")
{
    private readonly Queue<byte> _recent = new();
    private readonly List<byte> _frame = [];
    private readonly Lock _lock = new();

    public long Frames { get; private set; }
    public long FramesCorrect { get; private set; }

    public string Recent
    {
        get
        {
            lock (_lock) return string.Join(' ', _recent.Select(b => b.ToString("D3")));
        }
    }

    protected override bool Consume(ReadOnlySpan<byte> input)
    {
        lock (_lock)
        {
            foreach (var b in input)
            {
                _recent.Enqueue(b);
                if (_recent.Count > 32) _recent.Dequeue();
                _frame.Add(b);
                if (_frame.Count == payload.Length)
                {
                    Frames++;
                    if (_frame.SequenceEqual(payload)) FramesCorrect++;
                    _frame.Clear();
                }
            }
        }
        return true;
    }
}

/// <summary>Fraction of samples whose power falls below a threshold (deep-fade statistics).</summary>
public sealed class FadeProbe(float thresholdDb = -10) : SinkBlock<float>("fade_probe")
{
    private double _fraction;

    public double DeepFadeFraction => Volatile.Read(ref _fraction);

    protected override bool Consume(ReadOnlySpan<float> input)
    {
        var f = _fraction;
        foreach (var db in input) f = 0.9995 * f + 0.0005 * (db < thresholdDb ? 1 : 0);
        Volatile.Write(ref _fraction, f);
        return true;
    }
}
