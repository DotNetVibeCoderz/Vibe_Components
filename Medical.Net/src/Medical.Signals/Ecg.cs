using Medical.Core;
using Medical.Native;

namespace Medical.Signals;

public enum Rhythm { NormalSinus, SinusTachycardia, SinusBradycardia, AtrialFibrillation, Ectopy }

/// <summary>A sampled single-lead signal (millivolts for ECG).</summary>
public sealed record Signal(float[] Samples, double SampleRate, string Unit = "mV")
{
    public double DurationSeconds => Samples.Length / SampleRate;
    public double TimeAt(int index) => index / SampleRate;
}

/// <summary>Result of <see cref="EcgAnalyzer.Analyze"/>.</summary>
public sealed record EcgAnalysis(
    int[] RPeaks,
    double[] RrIntervalsMs,
    double HeartRateBpm,
    double SdnnMs,
    double RmssdMs,
    double Pnn50,
    double RrCv,
    Rhythm Rhythm,
    Localized Interpretation,
    KernelBackend Backend);

/// <summary>Synthetic ECG generator (sum-of-Gaussians P-QRS-T morphology, McSharry-style) with rhythm variants and noise.</summary>
public static class EcgSimulator
{
    private readonly record struct Wave(double Phase, double Amplitude, double Width);

    // Phase within the RR interval (0..1), amplitude in mV, width in fraction of RR.
    private static readonly Wave[] Normal =
    [
        new(0.16, 0.12, 0.025), // P
        new(0.27, -0.10, 0.008), // Q
        new(0.30, 1.20, 0.010), // R
        new(0.33, -0.25, 0.009), // S
        new(0.58, 0.30, 0.045), // T
    ];

    /// <summary>
    /// Generates <paramref name="seconds"/> of lead-II-like ECG at <paramref name="sampleRate"/> Hz.
    /// AF removes P waves, adds fibrillatory baseline and irregular RR; ectopy inserts premature wide beats.
    /// </summary>
    public static Signal Generate(double seconds = 10, double heartRate = 72, Rhythm rhythm = Rhythm.NormalSinus, double sampleRate = 250, double noise = 0.02, ulong seed = 1)
    {
        var r = new SeededRandom(seed);
        int n = (int)(seconds * sampleRate);
        var x = new float[n];
        double baseRr = 60.0 / heartRate;
        // 1) R-peak times. Ectopy: every 5th beat is a premature ventricular complex (coupling interval 0.62 RR)
        //    followed by a compensatory pause (1.38 RR), so the surrounding sinus rhythm is preserved.
        var beats = new List<(double TimeR, bool Ectopic)>();
        double tR = 0.3 * baseRr;
        for (int k = 0; tR < seconds + baseRr; k++)
        {
            bool ectopic = rhythm == Rhythm.Ectopy && k % 5 == 3;
            beats.Add((tR, ectopic));
            double rr = rhythm switch
            {
                Rhythm.AtrialFibrillation => baseRr * r.Uniform(0.65, 1.35),
                _ => baseRr * (1 + 0.03 * Math.Sin(2 * Math.PI * tR / 4.0) + r.Gaussian(0, 0.01)), // respiratory sinus arrhythmia
            };
            if (rhythm == Rhythm.Ectopy && (k + 1) % 5 == 3) rr *= 0.62;
            if (ectopic) rr *= 1.38;
            tR += rr;
        }
        // 2) Morphology around each R peak, with wave offsets/widths in seconds (fixed by the baseline rate, as in real ECGs).
        foreach (var (time, ectopic) in beats)
        {
            int from = Math.Max(0, (int)((time - 0.4 * baseRr) * sampleRate)), to = Math.Min(n, (int)((time + 0.6 * baseRr) * sampleRate));
            for (int i = from; i < to; i++)
            {
                double dt = (i / sampleRate - time) / baseRr; // offset from R in units of the baseline RR
                double v = 0;
                foreach (var w in Normal)
                {
                    if (rhythm == Rhythm.AtrialFibrillation && w.Phase < 0.2) continue; // no P wave
                    if (ectopic && w.Phase < 0.2) continue;                            // no preceding P wave
                    double amp = w.Amplitude, width = w.Width;
                    if (ectopic && w.Phase is > 0.25 and < 0.35) { width *= 1.8; amp *= 1.7; } // wide, tall QRS
                    if (ectopic && w.Phase > 0.5) amp = -amp;                                     // discordant T wave
                    double d = (dt - (w.Phase - 0.30)) / width;
                    v += amp * Math.Exp(-0.5 * d * d);
                }
                x[i] += (float)v;
            }
        }
        for (int i = 0; i < n; i++)
        {
            double time = i / sampleRate;
            double wander = 0.05 * Math.Sin(2 * Math.PI * 0.25 * time);
            double fib = rhythm == Rhythm.AtrialFibrillation ? 0.05 * Math.Sin(2 * Math.PI * 6.3 * time) + 0.03 * Math.Sin(2 * Math.PI * 7.9 * time + 1.1) : 0;
            x[i] += (float)(wander + fib + r.Gaussian(0, noise));
        }
        return new Signal(x, sampleRate);
    }

    /// <summary>Photoplethysmogram (pulse-oximeter waveform) synchronised to a heart rate.</summary>
    public static Signal Ppg(double seconds = 10, double heartRate = 72, double sampleRate = 100, ulong seed = 2)
    {
        var r = new SeededRandom(seed);
        int n = (int)(seconds * sampleRate);
        var x = new float[n];
        double rr = 60.0 / heartRate;
        for (int i = 0; i < n; i++)
        {
            double t = i / sampleRate, p = t % rr / rr;
            double systolic = Math.Exp(-0.5 * Math.Pow((p - 0.25) / 0.08, 2));
            double dicrotic = 0.35 * Math.Exp(-0.5 * Math.Pow((p - 0.55) / 0.07, 2));
            x[i] = (float)(systolic + dicrotic + 0.02 * Math.Sin(2 * Math.PI * 0.2 * t) + r.Gaussian(0, 0.01));
        }
        return new Signal(x, sampleRate, "a.u.");
    }
}

/// <summary>Simple zero-allocation-per-sample filters for biosignals.</summary>
public static class Filters
{
    /// <summary>Centered moving average of odd width (edges use the available window).</summary>
    public static float[] MovingAverage(ReadOnlySpan<float> x, int width)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        int half = width / 2;
        var y = new float[x.Length];
        double acc = 0;
        int lo = 0, hi = -1;
        for (int i = 0; i < x.Length; i++)
        {
            int newLo = Math.Max(0, i - half), newHi = Math.Min(x.Length - 1, i + half);
            while (hi < newHi) acc += x[++hi];
            while (lo < newLo) acc -= x[lo++];
            y[i] = (float)(acc / (hi - lo + 1));
        }
        return y;
    }

    /// <summary>Baseline-wander removal: subtracts a ~0.6 s moving average (high-pass ≈ 0.7 Hz).</summary>
    public static float[] RemoveBaseline(Signal s)
    {
        var trend = MovingAverage(s.Samples, (int)(0.6 * s.SampleRate) | 1);
        var y = new float[s.Samples.Length];
        for (int i = 0; i < y.Length; i++) y[i] = s.Samples[i] - trend[i];
        return y;
    }

    /// <summary>Downsamples by averaging blocks of <paramref name="factor"/> samples (for plotting).</summary>
    public static float[] Decimate(ReadOnlySpan<float> x, int factor)
    {
        var y = new float[x.Length / factor];
        for (int i = 0; i < y.Length; i++)
        {
            float s = 0;
            for (int k = 0; k < factor; k++) s += x[i * factor + k];
            y[i] = s / factor;
        }
        return y;
    }
}

/// <summary>ECG analysis: R peaks (Rust Pan-Tompkins kernel), heart rate, HRV and rhythm screening.</summary>
public static class EcgAnalyzer
{
    public static EcgAnalysis Analyze(Signal ecg)
    {
        var clean = Filters.RemoveBaseline(ecg);
        var peaks = MedicalKernels.DetectRPeaks(clean, ecg.SampleRate, 0.3);
        var rr = new double[Math.Max(0, peaks.Length - 1)];
        for (int i = 1; i < peaks.Length; i++) rr[i - 1] = (peaks[i] - peaks[i - 1]) * 1000.0 / ecg.SampleRate;
        if (rr.Length < 2)
            return new EcgAnalysis(peaks, rr, 0, 0, 0, 0, 0, Rhythm.NormalSinus,
                new("Not enough beats detected to analyse.", "Jumlah denyut yang terdeteksi tidak cukup untuk dianalisis."), MedicalKernels.ActiveBackend);

        double mean = rr.Average();
        double sdnn = Math.Sqrt(rr.Sum(v => (v - mean) * (v - mean)) / (rr.Length - 1));
        double sumSq = 0; int nn50 = 0;
        for (int i = 1; i < rr.Length; i++)
        {
            double d = rr[i] - rr[i - 1];
            sumSq += d * d;
            if (Math.Abs(d) > 50) nn50++;
        }
        double rmssd = Math.Sqrt(sumSq / (rr.Length - 1));
        double pnn50 = (double)nn50 / (rr.Length - 1);
        double hr = 60000.0 / mean, cv = sdnn / mean;

        // Premature beats: RR < 80 % of the local median followed by a compensatory pause.
        double median = rr.Order().ElementAt(rr.Length / 2);
        int premature = 0;
        for (int i = 0; i + 1 < rr.Length; i++) if (rr[i] < 0.8 * median && rr[i + 1] > 1.1 * median) premature++;

        var (rhythm, text) = (cv, hr, premature) switch
        {
            _ when cv > 0.12 && premature < rr.Length / 6 => (Rhythm.AtrialFibrillation, new Localized(
                $"Irregularly irregular rhythm (RR CV {cv:P0}) at {hr:0} bpm — screen positive for atrial fibrillation; confirm with a 12-lead ECG.",
                $"Irama ireguler tidak teratur (KV RR {cv:P0}) {hr:0} bpm — skrining positif fibrilasi atrium; konfirmasi dengan EKG 12 sadapan.")),
            _ when premature > 0 => (Rhythm.Ectopy, new Localized(
                $"Sinus rhythm {hr:0} bpm with {premature} premature beat(s).", $"Irama sinus {hr:0} bpm dengan {premature} denyut prematur.")),
            _ when hr > 100 => (Rhythm.SinusTachycardia, new Localized($"Sinus tachycardia, {hr:0} bpm.", $"Takikardia sinus, {hr:0} bpm.")),
            _ when hr < 60 => (Rhythm.SinusBradycardia, new Localized($"Sinus bradycardia, {hr:0} bpm.", $"Bradikardia sinus, {hr:0} bpm.")),
            _ => (Rhythm.NormalSinus, new Localized($"Normal sinus rhythm, {hr:0} bpm.", $"Irama sinus normal, {hr:0} bpm.")),
        };
        return new EcgAnalysis(peaks, rr, Math.Round(hr, 1), Math.Round(sdnn, 1), Math.Round(rmssd, 1), Math.Round(pnn50, 3), Math.Round(cv, 3), rhythm, text, MedicalKernels.ActiveBackend);
    }
}
