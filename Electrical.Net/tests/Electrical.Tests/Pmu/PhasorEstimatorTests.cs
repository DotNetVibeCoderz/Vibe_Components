using System.Numerics;
using Electrical.Pmu;

namespace Electrical.Tests;

/// <summary>
/// C37.118.1-2011 (and -2014 amendment) steady-state and step requirements: P class TVE ≤ 1 % over f₀ ± 2 Hz, M class TVE ≤ 1 % over
/// f₀ ± 5 Hz (Fs ≥ 50), FE ≤ 0.005 Hz; 10 % magnitude step response time ≤ 1.7/f₀ (P class), overshoot ≤ 5 % of the step.
/// </summary>
public class PhasorEstimatorTests
{
    private static readonly Complex Ref = Complex.FromPolarCoordinates(1.0, 0.3);

    private static (double MaxTve, double MaxFe) Run(PhasorEstimator est, double f0, double df, bool threePhase)
    {
        double fs = est.Options.SampleRateHz;
        int n = (int)(fs * 0.6);
        double t0 = 0;
        var a = PhasorEstimator.Sinusoid(Ref, f0 + df, fs, n, t0);
        var list = threePhase
            ? est.EstimatePositiveSequence(a,
                PhasorEstimator.Sinusoid(Ref * Complex.FromPolarCoordinates(1, -2 * Math.PI / 3), f0 + df, fs, n, t0),
                PhasorEstimator.Sinusoid(Ref * Complex.FromPolarCoordinates(1, 2 * Math.PI / 3), f0 + df, fs, n, t0), t0)
            : est.Estimate(a, t0);
        Assert.True(list.Count >= 10);
        double tve = 0, fe = 0;
        foreach (var e in list)
        {
            // Reference phasor relative to the nominal cosine rotates at 2π·Δf.
            var x = Ref * Complex.FromPolarCoordinates(1, 2 * Math.PI * df * e.TimeS);
            tve = Math.Max(tve, PhasorEstimator.Tve(e.Phasor, x));
            fe = Math.Max(fe, Math.Abs(e.FrequencyHz - (f0 + df)));
        }
        return (tve, fe);
    }

    [Theory]
    [InlineData(50.0, 1000.0)]
    [InlineData(60.0, 960.0)]
    public void PClass_SteadyState_OffNominal2Hz(double f0, double fs)
    {
        var est = new PhasorEstimator(new PhasorEstimatorOptions { NominalFrequencyHz = f0, SampleRateHz = fs, ReportingRate = f0, Class = PmuClass.P });
        Assert.Equal(2 * (fs / f0 - 1), est.FilterOrder); // 2-cycle triangle
        foreach (double df in new[] { -2.0, -1.0, 0.0, 1.0, 2.0 })
        {
            var (tve, fe) = Run(est, f0, df, threePhase: false);
            Assert.True(tve < 0.01, $"P class single-phase TVE {tve:P3} at Δf = {df} Hz");
            var (tve3, fe3) = Run(est, f0, df, threePhase: true);
            Assert.True(tve3 < 1e-3, $"P class positive-sequence TVE {tve3:P3} at Δf = {df} Hz");
            Assert.True(fe3 < 0.005, $"P class FE {fe3} Hz at Δf = {df} Hz");
            Assert.True(fe < 0.05, $"P class single-phase FE {fe} Hz at Δf = {df} Hz");
        }
    }

    [Fact]
    public void PClass_WithoutCompensation_ShowsTriangleDroop()
    {
        // At Δf = 2 Hz (50 Hz) the 2-cycle triangle has gain sinc²(Δf/f₀) ≈ 0.9947 → uncompensated TVE ≈ 0.5 %.
        var o = new PhasorEstimatorOptions { NominalFrequencyHz = 50, SampleRateHz = 1000, ReportingRate = 50, MagnitudeCompensation = false };
        var est = new PhasorEstimator(o);
        Assert.InRange(est.FilterGain(2), 0.993, 0.996);
        var (tve, _) = Run(est, 50, 2, threePhase: true);
        Assert.InRange(tve, 0.004, 0.007);
    }

    [Fact]
    public void MClass_SteadyState_OffNominal5Hz()
    {
        var est = new PhasorEstimator(new PhasorEstimatorOptions { NominalFrequencyHz = 50, SampleRateHz = 1000, ReportingRate = 50, Class = PmuClass.M });
        foreach (double df in new[] { -5.0, -2.5, 0.0, 2.5, 5.0 })
        {
            var (tve, fe) = Run(est, 50, df, threePhase: false);
            Assert.True(tve < 0.01, $"M class TVE {tve:P3} at Δf = {df} Hz");
            Assert.True(fe < 0.01, $"M class single-phase FE {fe} Hz at Δf = {df} Hz");
            var (tve3, fe3) = Run(est, 50, df, threePhase: true);
            Assert.True(tve3 < 1e-3, $"M class positive-sequence TVE {tve3:P3} at Δf = {df} Hz");
            Assert.True(fe3 < 0.005, $"M class FE {fe3} Hz at Δf = {df} Hz");
        }
    }

    [Fact]
    public void Rocof_FrequencyRamp()
    {
        // 1 Hz/s ramp starting at 49 Hz (C37.118.1a ramp test range): positive sequence, P class.
        double f0 = 50, fs = 1000, rr = 1.0;
        var est = new PhasorEstimator(new PhasorEstimatorOptions { NominalFrequencyHz = f0, SampleRateHz = fs, ReportingRate = 50 });
        int n = 1000;
        double[] Phase(double shift)
        {
            var x = new double[n];
            for (int i = 0; i < n; i++) { double t = i / fs; x[i] = Math.Sqrt(2) * Math.Cos(2 * Math.PI * (49 * t + 0.5 * rr * t * t) + shift); }
            return x;
        }
        var list = est.EstimatePositiveSequence(Phase(0), Phase(-2 * Math.PI / 3), Phase(2 * Math.PI / 3), 0);
        foreach (var e in list)
        {
            Assert.Equal(49 + rr * e.TimeS, e.FrequencyHz, 2);
            Assert.InRange(e.RocofHzPerS, 0.95, 1.05);
        }
    }

    [Fact]
    public void PClass_MagnitudeStep5Percent_ResponseTimeAndOvershoot()
    {
        double f0 = 50, fs = 1000;
        var est = new PhasorEstimator(new PhasorEstimatorOptions { NominalFrequencyHz = f0, SampleRateHz = fs, ReportingRate = fs });
        int n = 400, step = 200;
        double tStep = step / fs;
        double[] Phase(double shift)
        {
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = (i >= step ? 1.05 : 1.0) * Math.Sqrt(2) * Math.Cos(2 * Math.PI * f0 * i / fs + shift);
            return x;
        }
        var a = Phase(0); var b = Phase(-2 * Math.PI / 3); var c = Phase(2 * Math.PI / 3);
        double firstOut = double.NaN, lastOut = double.NaN, maxMag = 0, minMag = double.MaxValue;
        for (int i = est.Margin; i < n - est.Margin; i++)
        {
            var e = est.EstimatePositiveSequenceAt(a, b, c, i, 0);
            var reference = Complex.FromPolarCoordinates(i >= step ? 1.05 : 1.0, 0);
            if (PhasorEstimator.Tve(e.Phasor, reference) > 0.01)
            {
                if (double.IsNaN(firstOut)) firstOut = e.TimeS;
                lastOut = e.TimeS;
            }
            maxMag = Math.Max(maxMag, e.Magnitude);
            minMag = Math.Min(minMag, e.Magnitude);
        }
        double responseTime = lastOut - firstOut + 1 / fs;
        // Triangle response: TVE > 1 % while 20 % … 79 % of the window weight lies past the step → ≈ 0.72/f₀ = 14.4 ms at 50 Hz.
        Assert.InRange(responseTime, 0.010, 1.7 / f0);
        Assert.InRange(firstOut, tStep - 1.0 / f0, tStep);            // response is centred on the step (no group delay)
        Assert.True(maxMag <= 1.05 + 0.05 * 0.05, $"overshoot {maxMag}");
        Assert.True(minMag >= 1.0 - 0.05 * 0.05, $"undershoot {minMag}");
    }
}
