using GnuRadio.Fft;

namespace GnuRadio.Extensions;

/// <summary>A signal found in a spectrum.</summary>
public sealed record DetectedSignal(double CenterFrequency, double Bandwidth, float PeakDb, float SnrDb);

/// <summary>
/// Finds occupied channels in power spectra (the detection stage of <c>gr-inspector</c>): estimates the noise
/// floor, marks bins above it by <see cref="ThresholdDb"/>, merges neighbours into signals and reports centre,
/// bandwidth, peak and SNR. Feed it float vectors from <see cref="LogPowerFft"/>; results go out on
/// message port <c>signals</c> and in <see cref="Latest"/>.
/// </summary>
public sealed class SignalDetector : SinkBlock<float>
{
    private readonly Lock _lock = new();
    private IReadOnlyList<DetectedSignal> _latest = [];

    public SignalDetector(int fftSize, double sampleRate, double centerFrequency = 0, float thresholdDb = 10, int minimumBins = 2)
        : base("signal_detector", fftSize)
    {
        FftSize = fftSize;
        SampleRate = sampleRate;
        CenterFrequency = centerFrequency;
        ThresholdDb = thresholdDb;
        MinimumBins = minimumBins;
        RegisterMessageOutput("signals");
    }

    public int FftSize { get; }
    public double SampleRate { get; }
    public double CenterFrequency { get; set; }
    public float ThresholdDb { get; set; }
    public int MinimumBins { get; set; }

    public IReadOnlyList<DetectedSignal> Latest
    {
        get
        {
            lock (_lock) return _latest;
        }
    }

    public event Action<IReadOnlyList<DetectedSignal>>? SignalsDetected;

    protected override bool Consume(ReadOnlySpan<float> input)
    {
        var frames = input.Length / FftSize;
        if (frames == 0) return true;
        var spectrum = input.Slice((frames - 1) * FftSize, FftSize);
        var signals = Detect(spectrum, SampleRate, CenterFrequency, ThresholdDb, MinimumBins);
        lock (_lock) _latest = signals;
        PublishMessage("signals", Pmt.Tuple(signals.Select(s => Pmt.Dict(
            ("freq", s.CenterFrequency), ("bw", s.Bandwidth), ("peak", (double)s.PeakDb), ("snr", (double)s.SnrDb))).ToArray()));
        SignalsDetected?.Invoke(signals);
        return true;
    }

    /// <summary>Detection on one 0-Hz-centred dB spectrum.</summary>
    public static IReadOnlyList<DetectedSignal> Detect(ReadOnlySpan<float> spectrumDb, double sampleRate, double centerFrequency = 0,
        float thresholdDb = 10, int minimumBins = 2)
    {
        var floor = Spectral.NoiseFloor(spectrumDb);
        var binWidth = sampleRate / spectrumDb.Length;
        var result = new List<DetectedSignal>();
        var i = 0;
        while (i < spectrumDb.Length)
        {
            if (spectrumDb[i] < floor + thresholdDb)
            {
                i++;
                continue;
            }
            var start = i;
            var peak = float.MinValue;
            double weighted = 0, weight = 0;
            while (i < spectrumDb.Length && spectrumDb[i] >= floor + thresholdDb * 0.5f)
            {
                peak = Math.Max(peak, spectrumDb[i]);
                var p = Math.Pow(10, spectrumDb[i] / 10);
                weighted += p * i;
                weight += p;
                i++;
            }
            var bins = i - start;
            if (bins < minimumBins) continue;
            var centerBin = weighted / weight;
            result.Add(new DetectedSignal(
                centerFrequency + (centerBin - spectrumDb.Length / 2.0) * binWidth,
                bins * binWidth,
                peak,
                peak - floor));
        }
        return result;
    }
}
