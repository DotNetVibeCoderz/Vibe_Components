using GnuRadio.Blocks;
using GnuRadio.Fft;
using GnuRadio.Filter;
using GnuRadio.Kernels;

namespace GnuRadio.Tests;

public class DspTests
{
    private static Complex32[] RandomComplex(int n, int seed = 1)
    {
        var r = new FastRandom(seed);
        return Enumerable.Range(0, n).Select(_ => r.NextComplexGaussian()).ToArray();
    }

    private static Complex32[] Dft(Complex32[] x, bool inverse = false)
    {
        var n = x.Length;
        var y = new Complex32[n];
        var sign = inverse ? 1 : -1;
        for (var k = 0; k < n; k++)
        {
            double re = 0, im = 0;
            for (var t = 0; t < n; t++)
            {
                var a = sign * 2 * Math.PI * k * t / n;
                re += x[t].Real * Math.Cos(a) - x[t].Imaginary * Math.Sin(a);
                im += x[t].Real * Math.Sin(a) + x[t].Imaginary * Math.Cos(a);
            }
            y[k] = new Complex32((float)re, (float)im);
        }
        return y;
    }

    private static void AssertClose(ReadOnlySpan<Complex32> expected, ReadOnlySpan<Complex32> actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True((expected[i] - actual[i]).Magnitude <= tolerance, $"index {i}: expected {expected[i]}, got {actual[i]}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(100)]
    [InlineData(127)]
    public void FftMatchesDft(int n)
    {
        var x = RandomComplex(n);
        AssertClose(Dft(x), FftMath.Forward(x), 1e-3f * MathF.Sqrt(n));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(360)]
    public void InverseFftRoundTrips(int n)
    {
        var x = RandomComplex(n, 7);
        AssertClose(x, FftMath.Inverse(FftMath.Forward(x)), 1e-4f);
    }

    [Fact]
    public void ComplexMultiplyKernelMatchesScalar()
    {
        var a = RandomComplex(1001, 2);
        var b = RandomComplex(1001, 3);
        var dst = new Complex32[1001];
        Volk.Multiply(a, b, dst);
        AssertClose(a.Zip(b, (x, y) => x * y).ToArray(), dst, 1e-5f);
    }

    [Fact]
    public void DotKernelsMatchScalar()
    {
        var x = RandomComplex(203, 4);
        var taps = RandomComplex(203, 5);
        var expected = Complex32.Zero;
        for (var i = 0; i < x.Length; i++) expected += x[i] * taps[i];
        Assert.True((Volk.Dot(x, taps) - expected).Magnitude < 1e-3f);

        var real = taps.Select(t => t.Real).ToArray();
        var expectedReal = Complex32.Zero;
        for (var i = 0; i < x.Length; i++) expectedReal += x[i] * real[i];
        Assert.True((Volk.DotComplexReal(x, Volk.Interleave(real)) - expectedReal).Magnitude < 1e-3f);
    }

    [Fact]
    public void MagnitudeSquaredKernelMatchesScalar()
    {
        var x = RandomComplex(77, 6);
        var dst = new float[77];
        Volk.MagnitudeSquared(x, dst);
        for (var i = 0; i < x.Length; i++) Assert.Equal(x[i].MagnitudeSquared, dst[i], 4);
    }

    [Fact]
    public void FirFilterMatchesDirectConvolutionAcrossChunks()
    {
        var taps = Firdes.LowPass(1, 1, 0.1, 0.05);
        var x = RandomComplex(5000, 8);
        var filter = new FirFilter<Complex32>(taps);
        var y = new Complex32[x.Length];
        // Uneven chunk sizes exercise the history carry-over.
        var pos = 0;
        foreach (var chunk in new[] { 1, 13, 700, 2000, 2286 })
        {
            filter.Filter(x.AsSpan(pos, chunk), y.AsSpan(pos, chunk));
            pos += chunk;
        }
        for (var n = 0; n < x.Length; n += 97)
        {
            var e = Complex32.Zero;
            for (var k = 0; k < taps.Length; k++)
                if (n - k >= 0) e += x[n - k] * taps[k];
            Assert.True((y[n] - e).Magnitude < 1e-4f, $"sample {n}");
        }
    }

    [Fact]
    public void LowPassHasUnityDcGainAndAttenuatesStopband()
    {
        var taps = Firdes.LowPass(1, 48_000, 3_000, 1_000);
        Assert.Equal(1.0, taps.Sum(), 4);
        Assert.Equal(1, taps.Length % 2);
        var response = Firdes.MagnitudeResponseDb(taps, 481);
        Assert.True(response[(int)(6_000 / 24_000.0 * 480)] < -45, "stop band should be below -45 dB");
    }

    [Fact]
    public void RootRaisedCosineIsSymmetricAndNormalised()
    {
        var taps = Firdes.RootRaisedCosine(1, 4, 1, 0.35, 45);
        Assert.Equal(45, taps.Length);
        Assert.Equal(1.0, taps.Sum(), 4);
        for (var i = 0; i < taps.Length; i++) Assert.Equal(taps[i], taps[^(i + 1)], 5);
    }

    [Fact]
    public void RationalResamplerHasExpectedRateAndPreservesTone()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<float>();
        fg.Connect(new SignalSource<float>(48_000, Waveform.Sine, 1_000), new Head<float>(48_000),
            new RationalResampler<float>(3, 2), sink);
        fg.Run();
        var y = sink.Data;
        Assert.InRange(y.Length, 71_900, 72_000);
        // Count zero crossings in the settled middle: ~2 per cycle at 1 kHz over 0.5 s.
        var crossings = 0;
        for (var i = 20_000; i < 56_000; i++)
            if (Math.Sign(y[i]) != Math.Sign(y[i - 1])) crossings++;
        Assert.InRange(crossings, 990, 1010);
    }

    [Fact]
    public void ArbitraryResamplerHitsTheRequestedRate()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<Complex32>();
        fg.Connect(new SignalSource<Complex32>(1e6, Waveform.Cosine, 10e3), new Head<Complex32>(100_000),
            new ArbitraryResampler<Complex32>(0.7371), sink);
        fg.Run();
        Assert.InRange(sink.Count, 73_600, 73_720);
        var mags = sink.Data.Skip(1000).Take(60_000).Select(c => c.Magnitude).ToArray();
        Assert.InRange(mags.Average(), 0.95, 1.05);
    }

    [Fact]
    public void FreqXlatingFilterMovesToneToDc()
    {
        var fg = new Flowgraph();
        var taps = Firdes.LowPass(1, 1e6, 20e3, 10e3);
        var xlate = new FreqXlatingFirFilter(10, taps, 200e3, 1e6);
        var sink = new VectorSink<Complex32>();
        fg.Connect(new SignalSource<Complex32>(1e6, Waveform.Cosine, 205e3), new Head<Complex32>(200_000), xlate, sink);
        fg.Run();
        var y = sink.Data.Skip(500).ToArray();
        // The tone should now sit at +5 kHz in a 100 kS/s stream: phase advance 2π·0.05 per sample.
        var advance = 0.0;
        for (var i = 1; i < y.Length; i++) advance += (y[i] * y[i - 1].Conjugate()).Phase;
        Assert.Equal(2 * Math.PI * 5e3 / 100e3, advance / (y.Length - 1), 3);
        Assert.InRange(y.Average(c => c.Magnitude), 0.95, 1.05);
    }

    [Fact]
    public void ButterworthHasMinus3DbAtCutoff()
    {
        var sections = IirDesign.ButterworthLowPass(5, 1_000, 48_000);
        var mag = sections.Aggregate(1.0, (m, s) => m * s.Magnitude(1_000 / 48_000.0));
        Assert.Equal(-3.01, 20 * Math.Log10(mag), 1);
    }

    [Fact]
    public void LogPowerFftReadsZeroDbForUnitTone()
    {
        var fg = new Flowgraph();
        var fft = new LogPowerFft(1e6, 1024, frameRate: 1e9);
        var sink = new VectorSink<float>(1024);
        fg.Connect(new SignalSource<Complex32>(1e6, Waveform.Cosine, 250e3), new Head<Complex32>(1024 * 4), fft, sink);
        fg.Run();
        var frame = sink.Data.AsSpan(0, 1024);
        var (index, value) = Volk.ArgMax(frame);
        Assert.Equal(512 + 256, index);
        Assert.Equal(0, value, 0.5);
    }

    [Fact]
    public void WelchFindsTheTone()
    {
        var src = new Complex32[16384];
        for (var i = 0; i < src.Length; i++) src[i] = Complex32.FromPolar(1, (float)(2 * Math.PI * 0.125 * i));
        var psd = Spectral.WelchPsd(src, 1.0, 512);
        Assert.Equal(256 + 64, Volk.ArgMax(psd).Index);
    }
}
