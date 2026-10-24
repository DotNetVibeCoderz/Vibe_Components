using GnuRadio.Fft;
using GnuRadio.Filter;

namespace GnuRadio.Extensions;

/// <summary>A point target for the radar simulator.</summary>
public sealed record RadarTarget(double Range, double Velocity, double Rcs = 1.0);

/// <summary>FMCW chirp parameters (as used by <c>gr-radar</c>'s FMCW examples).</summary>
public sealed record FmcwParameters(
    double CarrierHz = 24e9,
    double SweepBandwidth = 150e6,
    double ChirpDuration = 1e-3,
    double SampleRate = 512e3)
{
    public const double SpeedOfLight = 299_792_458.0;

    public int SamplesPerChirp => (int)Math.Round(ChirpDuration * SampleRate);

    /// <summary>Range resolution c/(2B).</summary>
    public double RangeResolution => SpeedOfLight / (2 * SweepBandwidth);

    /// <summary>Beat frequency of a target at <paramref name="range"/>: 2RB/(cT).</summary>
    public double BeatFrequency(double range) => 2 * range * SweepBandwidth / (SpeedOfLight * ChirpDuration);

    /// <summary>Range that produces a beat of <paramref name="frequency"/> Hz.</summary>
    public double RangeOf(double frequency) => frequency * SpeedOfLight * ChirpDuration / (2 * SweepBandwidth);

    public double MaxRange => RangeOf(SampleRate / 2);
    public double Wavelength => SpeedOfLight / CarrierHz;
}

/// <summary>
/// Simulates the de-chirped (beat) signal of an FMCW radar looking at point targets, chirp after chirp, with
/// receiver noise. Each target contributes a complex tone at its beat frequency whose phase advances chirp to
/// chirp with its Doppler (ranges stay fixed, so a scene is stable). Targets can be changed while running.
/// </summary>
public sealed class FmcwRadarSource : SourceBlock<Complex32>
{
    private readonly FmcwParameters _p;
    private readonly FastRandom _rng;
    private long _sample;
    private volatile RadarTarget[] _targets;

    public FmcwRadarSource(FmcwParameters parameters, IEnumerable<RadarTarget> targets, float noise = 0.05f, long seed = 0)
        : base("fmcw_radar_source")
    {
        _p = parameters;
        _targets = targets.ToArray();
        Noise = noise;
        _rng = new FastRandom(seed);
    }

    public float Noise { get; set; }

    public IReadOnlyList<RadarTarget> Targets
    {
        get => _targets;
        set => _targets = [.. value];
    }

    protected override int Generate(Span<Complex32> output)
    {
        var spc = _p.SamplesPerChirp;
        var targets = _targets;
        for (var i = 0; i < output.Length; i++)
        {
            var n = _sample + i;
            var chirp = n / spc;
            var t = (n % spc) / _p.SampleRate;
            Complex32 acc = default;
            foreach (var target in targets)
            {
                // Range is held fixed (a target "on station"); its radial velocity shows up as the Doppler phase
                // progression from chirp to chirp, which is what range-Doppler processing measures.
                var range = target.Range;
                var beat = _p.BeatFrequency(range);
                var doppler = 2 * target.Velocity / _p.Wavelength;
                var elapsed = chirp * _p.ChirpDuration + t;
                var phase = 2 * Math.PI * (beat * t + 2 * range / _p.Wavelength + doppler * elapsed);
                var amplitude = (float)(Math.Sqrt(target.Rcs) / Math.Max(1, range / 10));
                acc += Complex32.FromPolar(amplitude, (float)(phase % (2 * Math.PI)));
            }
            output[i] = acc + _rng.NextComplexGaussian() * Noise;
        }
        _sample += output.Length;
        return output.Length;
    }
}

/// <summary>
/// Range FFT per chirp (GNU Radio <c>gr-radar</c>'s range processing): outputs one vector of
/// <see cref="FftSize"/>/2 range-bin powers in dB per chirp (positive beat frequencies only).
/// </summary>
public sealed class FmcwRangeProcessor : Block
{
    private readonly FmcwParameters _p;
    private readonly FftPlan _plan;
    private readonly float[] _window;
    private readonly Complex32[] _buffer;

    public FmcwRangeProcessor(FmcwParameters parameters) : base("fmcw_range_fft")
    {
        _p = parameters;
        FftSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)parameters.SamplesPerChirp);
        _plan = new FftPlan(FftSize);
        _window = Window.Build(WindowType.Hann, parameters.SamplesPerChirp);
        _buffer = new Complex32[FftSize];
        RelativeRate = 1.0 / parameters.SamplesPerChirp;
        AddInput<Complex32>();
        AddOutput<float>(FftSize / 2);
    }

    public int FftSize { get; }

    /// <summary>Range in metres of output bin <paramref name="bin"/>.</summary>
    public double BinRange(int bin) => _p.RangeOf(bin * _p.SampleRate / FftSize);

    protected internal override int MinimumInputItems => _p.SamplesPerChirp;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var spc = _p.SamplesPerChirp;
        var chirps = Math.Min(context.InputItems(0) / spc, context.OutputItems(0));
        var input = context.Input<Complex32>(0);
        var output = context.Output<float>(0);
        var half = FftSize / 2;
        for (var c = 0; c < chirps; c++)
        {
            Array.Clear(_buffer);
            for (var i = 0; i < spc; i++) _buffer[i] = input[c * spc + i] * _window[i];
            _plan.Execute(_buffer);
            var row = output.Slice(c * half, half);
            for (var k = 0; k < half; k++) row[k] = 10f * MathF.Log10(_buffer[k].MagnitudeSquared / (spc * (float)spc) + 1e-20f);
        }
        context.Consume(0, chirps * spc);
        context.Produce(0, chirps);
        return WorkResult.Ok;
    }
}

/// <summary>Cell-averaging CFAR detection on a power profile in dB.</summary>
public static class Cfar
{
    /// <summary>
    /// Indices of cells exceeding the mean of the surrounding training cells by <paramref name="thresholdDb"/>,
    /// keeping only local maxima.
    /// </summary>
    public static int[] Detect(ReadOnlySpan<float> profileDb, int training = 8, int guard = 2, double thresholdDb = 12)
    {
        var hits = new List<int>();
        for (var i = 0; i < profileDb.Length; i++)
        {
            double sum = 0;
            var count = 0;
            for (var j = i - guard - training; j <= i + guard + training; j++)
            {
                if (j < 0 || j >= profileDb.Length || Math.Abs(j - i) <= guard) continue;
                sum += Math.Pow(10, profileDb[j] / 10);
                count++;
            }
            if (count == 0) continue;
            var noiseDb = 10 * Math.Log10(sum / count);
            var isPeak = (i == 0 || profileDb[i] >= profileDb[i - 1]) && (i == profileDb.Length - 1 || profileDb[i] >= profileDb[i + 1]);
            if (isPeak && profileDb[i] > noiseDb + thresholdDb) hits.Add(i);
        }
        return [.. hits];
    }
}

/// <summary>Offline range–Doppler processing of a block of chirps.</summary>
public static class RangeDoppler
{
    /// <summary>
    /// 2-D FFT: range FFT per chirp, then Doppler FFT across chirps per range bin. Returns [doppler][range]
    /// power in dB with zero velocity in the middle row.
    /// </summary>
    public static float[][] Compute(ReadOnlySpan<Complex32> samples, FmcwParameters p, int chirps)
    {
        var spc = p.SamplesPerChirp;
        var rangeBins = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)spc);
        var dopplerBins = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)chirps);
        var w = Window.Build(WindowType.Hann, spc);
        var rangePlan = FftPlan.Get(rangeBins);
        var cube = new Complex32[dopplerBins][];
        for (var c = 0; c < dopplerBins; c++)
        {
            cube[c] = new Complex32[rangeBins];
            if (c >= chirps) continue;
            for (var i = 0; i < spc; i++) cube[c][i] = samples[c * spc + i] * w[i];
            rangePlan.Execute(cube[c]);
        }
        var dopplerPlan = FftPlan.Get(dopplerBins);
        var column = new Complex32[dopplerBins];
        var result = new float[dopplerBins][];
        for (var d = 0; d < dopplerBins; d++) result[d] = new float[rangeBins / 2];
        for (var r = 0; r < rangeBins / 2; r++)
        {
            for (var c = 0; c < dopplerBins; c++) column[c] = cube[c][r];
            dopplerPlan.Execute(column);
            FftMath.Shift(column.AsSpan());
            for (var d = 0; d < dopplerBins; d++) result[d][r] = 10f * MathF.Log10(column[d].MagnitudeSquared + 1e-20f);
        }
        return result;
    }
}
