using GnuRadio.Blocks;
using GnuRadio.Filter;
using GnuRadio.Kernels;

namespace GnuRadio.Channels;

/// <summary>Adds complex white Gaussian noise of standard deviation <see cref="NoiseVoltage"/> (live adjustable).</summary>
public sealed class AwgnChannel(float noiseVoltage, long seed = 0) : SyncBlock<Complex32, Complex32>("awgn_channel")
{
    private readonly FastRandom _rng = new(seed);
    private volatile float _noise = noiseVoltage;

    public float NoiseVoltage
    {
        get => _noise;
        set => _noise = value;
    }

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        var a = _noise;
        if (a == 0)
        {
            input.CopyTo(output);
            return;
        }
        for (var i = 0; i < input.Length; i++) output[i] = input[i] + _rng.NextComplexGaussian() * a;
    }
}

/// <summary>Constant frequency offset in Hz, live adjustable (a carrier mismatch between transmitter and receiver).</summary>
public sealed class FrequencyOffset(double sampleRate, double offsetHz) : SyncBlock<Complex32, Complex32>("frequency_offset")
{
    private Complex32 _phase = Complex32.One;
    private double _offset = offsetHz;

    public double SampleRate { get; } = sampleRate;

    public double OffsetHz
    {
        get => Volatile.Read(ref _offset);
        set => Volatile.Write(ref _offset, value);
    }

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        var inc = Complex32.FromPolar(1f, (float)(2 * Math.PI * OffsetHz / SampleRate));
        Volk.Rotate(input, inc, ref _phase, output);
    }
}

/// <summary>
/// Doppler shift of a moving transmitter: f_d = v/c·f_c, with an optional linear drift (Hz/s) such as a
/// satellite pass. <see cref="CurrentShiftHz"/> reports the instantaneous shift.
/// </summary>
public sealed class DopplerShift : SyncBlock<Complex32, Complex32>
{
    private const double SpeedOfLight = 299_792_458.0;
    private double _phase;
    private long _samples;

    public DopplerShift(double sampleRate, double carrierHz, double radialVelocity, double driftHzPerSecond = 0) : base("doppler_shift")
    {
        SampleRate = sampleRate;
        CarrierHz = carrierHz;
        RadialVelocity = radialVelocity;
        DriftHzPerSecond = driftHzPerSecond;
    }

    public double SampleRate { get; }
    public double CarrierHz { get; set; }

    /// <summary>Velocity toward the receiver in m/s (positive raises the frequency).</summary>
    public double RadialVelocity { get; set; }

    public double DriftHzPerSecond { get; set; }

    public double CurrentShiftHz => RadialVelocity / SpeedOfLight * CarrierHz + DriftHzPerSecond * _samples / SampleRate;

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        var baseShift = RadialVelocity / SpeedOfLight * CarrierHz;
        var drift = DriftHzPerSecond / SampleRate;
        for (var i = 0; i < input.Length; i++)
        {
            var f = baseShift + drift * (_samples + i);
            _phase += 2 * Math.PI * f / SampleRate;
            if (_phase > Math.PI) _phase -= 2 * Math.PI;
            else if (_phase < -Math.PI) _phase += 2 * Math.PI;
            output[i] = input[i] * Complex32.FromPolar(1f, (float)_phase);
        }
        _samples += input.Length;
    }
}

/// <summary>
/// Flat Rayleigh or Rician fading by the sum-of-sinusoids method of Zheng &amp; Xiao (GNU Radio's
/// <c>channels.fading_model</c>). <c>fdTs</c> is the maximum Doppler times the sample period; with
/// <c>los</c> true, <c>kFactor</c> sets the ratio of line-of-sight to scattered power.
/// </summary>
public sealed class FadingModel : SyncBlock<Complex32, Complex32>
{
    private readonly FadingProcess _process;

    public FadingModel(int sinusoids = 8, double fdTs = 1e-4, bool los = false, double kFactor = 4, long seed = 0) : base("fading_model")
    {
        _process = new FadingProcess(sinusoids, fdTs, los, kFactor, seed);
    }

    /// <summary>The most recent channel gain, for plotting the fading envelope.</summary>
    public Complex32 CurrentGain => _process.Last;

    public double FdTs
    {
        get => _process.FdTs;
        set => _process.FdTs = value;
    }

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = input[i] * _process.Next();
    }
}

/// <summary>Sum-of-sinusoids generator of one unit-power fading coefficient per sample.</summary>
public sealed class FadingProcess
{
    private readonly int _m;
    private readonly double[] _alpha;
    private readonly double[] _phiI;
    private readonly double[] _phiQ;
    private readonly double _theta;
    private readonly bool _los;
    private readonly double _k;
    private readonly FastRandom _rng;
    private long _n;

    public FadingProcess(int sinusoids, double fdTs, bool los, double kFactor, long seed)
    {
        _m = sinusoids;
        FdTs = fdTs;
        _los = los;
        _k = kFactor;
        _rng = new FastRandom(seed);
        var thetaBase = (2 * _rng.NextDouble() - 1) * Math.PI;
        _alpha = new double[sinusoids];
        _phiI = new double[sinusoids];
        _phiQ = new double[sinusoids];
        for (var i = 0; i < sinusoids; i++)
        {
            _alpha[i] = (2 * Math.PI * (i + 1) - Math.PI + thetaBase) / (4 * sinusoids);
            _phiI[i] = (2 * _rng.NextDouble() - 1) * Math.PI;
            _phiQ[i] = (2 * _rng.NextDouble() - 1) * Math.PI;
        }
        _theta = (2 * _rng.NextDouble() - 1) * Math.PI;
    }

    public double FdTs { get; set; }
    public Complex32 Last { get; private set; } = Complex32.One;

    public Complex32 Next()
    {
        var wd = 2 * Math.PI * FdTs;
        double re = 0, im = 0;
        for (var i = 0; i < _m; i++)
        {
            re += Math.Cos(wd * _n * Math.Cos(_alpha[i]) + _phiI[i]);
            im += Math.Cos(wd * _n * Math.Sin(_alpha[i]) + _phiQ[i]);
        }
        var scale = Math.Sqrt(2.0 / _m) / Math.Sqrt(2.0);
        re *= scale;
        im *= scale;
        if (_los)
        {
            var los = Math.Sqrt(_k / (_k + 1));
            var scatter = 1 / Math.Sqrt(_k + 1);
            var phase = wd * _n * Math.Cos(_theta) + _theta;
            re = scatter * re + los * Math.Cos(phase);
            im = scatter * im + los * Math.Sin(phase);
        }
        _n++;
        return Last = new Complex32((float)re, (float)im);
    }
}

/// <summary>
/// Frequency-selective fading (GNU Radio's <c>channels.selective_fading_model</c>): several paths at
/// fractional delays (in samples), each with its own independent fading process and mean magnitude.
/// The combined time-varying impulse response is rebuilt every <c>updateInterval</c> samples.
/// </summary>
public sealed class SelectiveFadingModel : Block
{
    private readonly FadingProcess[] _paths;
    private readonly float[][] _pathTaps;
    private readonly float[] _magnitudes;
    private readonly Complex32[] _taps;
    private readonly Complex32[] _reversed;
    private readonly int _updateInterval;
    private Complex32[] _work;
    private int _sinceUpdate;

    public SelectiveFadingModel(double[] delays, double[] magnitudes, int sinusoids = 8, double fdTs = 1e-4,
        bool los = false, double kFactor = 4, int ntaps = 16, long seed = 0, int updateInterval = 16)
        : base("selective_fading_model")
    {
        if (delays.Length != magnitudes.Length) throw new ArgumentException("delays and magnitudes must have the same length.");
        _paths = new FadingProcess[delays.Length];
        _pathTaps = new float[delays.Length][];
        _magnitudes = magnitudes.Select(m => (float)m).ToArray();
        var maxDelay = delays.Length == 0 ? 0 : delays.Max();
        if (maxDelay > ntaps - 4) throw new ArgumentException("ntaps must exceed the largest delay by a few samples.", nameof(ntaps));
        // Centre the delay profile in the tap window so every path's sinc pulse is inside the window's main lobe.
        BulkDelay = Math.Floor((ntaps - 1 - maxDelay) / 2);
        for (var p = 0; p < delays.Length; p++)
        {
            _paths[p] = new FadingProcess(sinusoids, fdTs, los && p == 0, kFactor, seed == 0 ? 0 : seed + p * 7919);
            // Windowed-sinc fractional delay.
            var taps = new float[ntaps];
            var window = Window.Build(WindowType.Hamming, ntaps);
            for (var k = 0; k < ntaps; k++)
            {
                var x = k - delays[p] - BulkDelay;
                taps[k] = (float)((Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x)) * window[k]);
            }
            _pathTaps[p] = taps;
        }
        var power = _magnitudes.Sum(m => m * m);
        for (var p = 0; p < _magnitudes.Length; p++) _magnitudes[p] /= MathF.Sqrt(power);
        _taps = new Complex32[ntaps];
        _reversed = new Complex32[ntaps];
        _updateInterval = Math.Max(1, updateInterval);
        _work = new Complex32[ntaps - 1];
        AddInput<Complex32>();
        AddOutput<Complex32>();
        UpdateTaps();
    }

    /// <summary>Fixed delay in samples added so the fractional-delay filters stay causal.</summary>
    public double BulkDelay { get; }

    /// <summary>The current combined impulse response.</summary>
    public ReadOnlySpan<Complex32> Taps => _taps;

    private void UpdateTaps()
    {
        Array.Clear(_taps);
        for (var p = 0; p < _paths.Length; p++)
        {
            var g = _paths[p].Next() * _magnitudes[p];
            var t = _pathTaps[p];
            for (var k = 0; k < t.Length; k++) _taps[k] += g * t[k];
        }
        for (var k = 0; k < _taps.Length; k++) _reversed[k] = _taps[_taps.Length - 1 - k];
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.OutputItems(0));
        var input = context.Input<Complex32>(0)[..n];
        var output = context.Output<Complex32>(0);
        var h = _taps.Length - 1;
        if (_work.Length < h + n)
        {
            var w = new Complex32[Math.Max(h + n, _work.Length * 2)];
            _work.AsSpan(0, h).CopyTo(w);
            _work = w;
        }
        input.CopyTo(_work.AsSpan(h));
        var work = _work.AsSpan(0, h + n);
        for (var i = 0; i < n; i++)
        {
            if (++_sinceUpdate >= _updateInterval)
            {
                _sinceUpdate = 0;
                // Advance the fading processes by the interval so their Doppler rate stays correct.
                for (var s = 0; s < _updateInterval - 1; s++) foreach (var p in _paths) p.Next();
                UpdateTaps();
            }
            output[i] = Volk.Dot(work.Slice(i, _taps.Length), _reversed);
        }
        work.Slice(n, h).CopyTo(_work);
        context.Consume(0, n);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>
/// The classic all-in-one impairment block (GNU Radio's <c>channels.channel_model</c>): static multipath taps,
/// sample-clock offset <c>epsilon</c> (1.0 = none), carrier frequency offset (normalised to the sample rate)
/// and AWGN. Each stage is skipped when it is neutral.
/// </summary>
public sealed class ChannelModel : HierBlock
{
    public ChannelModel(float noiseVoltage = 0, double frequencyOffset = 0, double epsilon = 1.0, Complex32[]? taps = null, long seed = 0)
        : base("channel_model")
    {
        DeclareInput<Complex32>();
        DeclareOutput<Complex32>();
        var chain = new List<Block>();
        if (taps is { Length: > 0 } && !(taps.Length == 1 && taps[0] == Complex32.One)) chain.Add(new FirFilterCcc(1, taps));
        if (Math.Abs(epsilon - 1.0) > 1e-12) chain.Add(new ArbitraryResampler<Complex32>(1.0 / epsilon));
        Rotator = new Rotator(2 * Math.PI * frequencyOffset);
        chain.Add(Rotator);
        Noise = new AwgnChannel(noiseVoltage, seed);
        chain.Add(Noise);
        Connect([.. chain]);
        ConnectInput(0, chain[0]);
        ConnectOutput(0, chain[^1]);
    }

    /// <summary>Live control of the noise level.</summary>
    public AwgnChannel Noise { get; }

    /// <summary>Live control of the frequency offset (set <see cref="Blocks.Rotator.PhaseIncrement"/> = 2π·offset).</summary>
    public Rotator Rotator { get; }
}
