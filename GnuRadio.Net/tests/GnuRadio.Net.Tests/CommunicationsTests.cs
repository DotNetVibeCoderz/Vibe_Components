using GnuRadio.Analog;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Digital;
using GnuRadio.Fec;
using GnuRadio.Synchronization;

namespace GnuRadio.Tests;

public class CommunicationsTests
{
    private static byte[] RandomBytes(int n, int seed)
    {
        var b = new byte[n];
        new FastRandom(seed).NextBytes(b);
        return b;
    }

    private static double Correlation(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < n; i++)
        {
            ab += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }
        return ab / Math.Sqrt(aa * bb);
    }

    /// <summary>Best normalised correlation over lags 0..maxLag (b delayed relative to a).</summary>
    private static double BestCorrelation(float[] a, float[] b, int maxLag, int skip = 0)
    {
        var best = 0.0;
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var len = Math.Min(a.Length - skip, b.Length - skip - lag);
            if (len <= 0) break;
            var c = Correlation(a.AsSpan(skip, len).ToArray(), b.AsSpan(skip + lag, len).ToArray());
            best = Math.Max(best, c);
        }
        return best;
    }

    [Fact]
    public void FmModulateDemodulateRecoversAudio()
    {
        var audioRate = 48_000;
        var tone = Enumerable.Range(0, 48_000).Select(i => 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / audioRate)).ToArray();
        var fg = new Flowgraph();
        var sink = new VectorSink<float>();
        fg.Connect(new VectorSource<float>(tone), new FrequencyModulator(FrequencyModulator.SensitivityFor(audioRate, 5000)),
            new QuadratureDemod(QuadratureDemod.GainFor(audioRate, 5000)), sink);
        fg.Run();
        var y = sink.Data;
        for (var i = 100; i < y.Length; i += 501) Assert.Equal(tone[i], y[i], 2);
    }

    [Fact]
    public void WidebandFmChainRecoversTone()
    {
        var audioRate = 48_000.0;
        var quadRate = 240_000.0;
        var fg = new Flowgraph();
        var sink = new VectorSink<float>();
        var src = new SignalSource<float>(audioRate, Waveform.Sine, 1000, 0.7f);
        fg.Connect(src, new Head<float>(24_000), new FmTransmit(audioRate, quadRate), new WbfmReceive(quadRate, 5), sink);
        fg.Run();
        var y = sink.Data;
        var reference = Enumerable.Range(0, y.Length).Select(i => MathF.Sin(2 * MathF.PI * 1000 * (float)i / 48_000)).ToArray();
        Assert.True(BestCorrelation(reference, y, 200, 2000) > 0.95);
    }

    [Fact]
    public void AmDemodRecoversEnvelope()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<float>();
        fg.Connect(new SignalSource<float>(8000, Waveform.Sine, 200, 1f), new Head<float>(16_000), new AmModulator(0.5f),
            new AmDemod(0.002f), sink);
        fg.Run();
        var y = sink.Data;
        var reference = Enumerable.Range(0, y.Length).Select(i => MathF.Sin(2 * MathF.PI * 200 * i / 8000f)).ToArray();
        Assert.True(Correlation(reference[4000..], y[4000..]) > 0.99);
    }

    [Fact]
    public void SsbModulatorPutsEnergyOnOneSide()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<Complex32>();
        fg.Connect(new SignalSource<float>(8000, Waveform.Sine, 1000), new Head<float>(8192), new SsbModulator(Sideband.Upper), sink);
        fg.Run();
        var spectrum = Fft.Spectral.WelchPsd(sink.Data.AsSpan(1000), 8000, 512);
        var upper = spectrum[256 + 64];
        var lower = spectrum[256 - 64];
        Assert.True(upper - lower > 30, $"sideband suppression {upper - lower} dB");
    }

    [Fact]
    public void MorseRoundTripsThroughKeyerAndDecoder()
    {
        var fg = new Flowgraph();
        var decoder = new MorseDecoder(8000, 20);
        fg.Connect(new MorseKeyer("CQ DE YB0GRC", 8000, 20, repeat: false), decoder);
        fg.Run();
        Assert.Equal("CQ DE YB0GRC", decoder.Text.Trim());
    }

    [Fact]
    public void PllLocksToOffsetCarrier()
    {
        var fg = new Flowgraph();
        var pll = new Pll(2 * Math.PI / 100, 0.5, -0.5);
        fg.Connect(new SignalSource<Complex32>(1e5, Waveform.Cosine, 1500), new Head<Complex32>(50_000), pll, new NullSink<Complex32>());
        fg.Run();
        Assert.Equal(2 * Math.PI * 1500 / 1e5, pll.Frequency, 3);
        Assert.True(pll.LockError < 0.05f);
    }

    [Theory]
    [InlineData("bpsk")]
    [InlineData("qpsk")]
    [InlineData("8psk")]
    [InlineData("16qam")]
    [InlineData("64qam")]
    public void ConstellationDecisionsInvertMapping(string name)
    {
        var c = Constellation.FromName(name);
        Assert.Equal(1.0, c.Points.Average(p => p.MagnitudeSquared), 3);
        Span<float> llr = stackalloc float[c.BitsPerSymbol];
        for (var s = 0; s < c.Arity; s++)
        {
            Assert.Equal(s, c.Decide(c.Points[s] + new Complex32(0.01f, -0.01f)));
            c.SoftDecision(c.Points[s], 0.1f, llr);
            for (var b = 0; b < c.BitsPerSymbol; b++)
                Assert.Equal((s >> (c.BitsPerSymbol - 1 - b)) & 1, llr[b] < 0 ? 1 : 0);
        }
    }

    [Fact]
    public void ScramblersRoundTrip()
    {
        var bits = Bits.Unpack(RandomBytes(500, 4));
        var fg = new Flowgraph();
        var sink = new VectorSink<byte>();
        fg.Connect(new VectorSource<byte>(bits), new Scrambler(), new Descrambler(), new AdditiveScrambler(), new AdditiveScrambler(), sink);
        fg.Run();
        Assert.Equal(bits, sink.Data);
    }

    [Fact]
    public void RepackBitsRoundTrip()
    {
        var data = RandomBytes(300, 6);
        var fg = new Flowgraph();
        var sink = new VectorSink<byte>();
        fg.Connect(new VectorSource<byte>(data), new RepackBits(8, 3), new RepackBits(3, 8), sink);
        fg.Run();
        Assert.Equal(data[..^1], sink.Data[..(data.Length - 1)]);
    }

    [Theory]
    [InlineData("qpsk", 12.0, 0.0, 0.0)]
    [InlineData("qpsk", 12.0, 0.002, 0.0)]
    [InlineData("8psk", 16.0, 0.001, 0.0)]
    [InlineData("16qam", 20.0, 0.0005, 0.0)]
    [InlineData("bpsk", 8.0, 0.002, 1.0005)]
    public void LinearModemSynchronisesAndDecodes(string name, double ebn0, double freqOffset, double epsilon)
    {
        var c = Constellation.FromName(name);
        var sps = 4;
        var data = RandomBytes(6000, 11);
        var fg = new Flowgraph();
        var noise = BerMath.NoiseVoltage(ebn0, c.BitsPerSymbol, sps) / MathF.Sqrt(sps);
        var rx = new ConstellationReceiver(c, sps);
        var symbols = new VectorSink<Complex32>();
        fg.Connect(new VectorSource<byte>(data), new ConstellationModulator(c, sps, differential: false),
            new ChannelModel(noise, freqOffset, epsilon == 0 ? 1.0 : epsilon, seed: 5), rx, symbols);
        fg.Run();

        // After acquisition the constellation should be tight; compare against the nearest rotation
        // of the transmitted symbols (carrier recovery has a rotational ambiguity).
        var rxSyms = symbols.Data.Skip(2000).Take(8000).ToArray();
        var evm = c.Evm(rxSyms);
        Assert.True(evm < 0.25, $"EVM {evm:F3}");

        var txSymbols = Bits.Unpack(data).Chunk(c.BitsPerSymbol).Select(ch => ch.Aggregate(0, (acc, bit) => (acc << 1) | bit)).ToArray();
        var bestMatches = 0.0;
        for (var rot = 0; rot < c.RotationalSymmetry; rot++)
        {
            var r = Complex32.FromPolar(1, 2 * MathF.PI * rot / c.RotationalSymmetry);
            var rotated = rxSyms.Select(sym => c.Decide(sym * r)).ToArray();
            // rx symbol j corresponds to tx symbol j − D for a small pipeline delay D.
            for (var d = -60; d <= 60; d++)
            {
                var hits = 0;
                for (var i = 0; i < 500; i++)
                    if (rotated[i] == txSymbols[2000 + i - d]) hits++;
                bestMatches = Math.Max(bestMatches, hits / 500.0);
            }
        }
        Assert.True(bestMatches > 0.97, $"symbol agreement {bestMatches:P1}");
    }

    [Fact]
    public void FskModemRoundTrips()
    {
        var bits = Bits.Unpack(RandomBytes(400, 12));
        var fg = new Flowgraph();
        var sink = new VectorSink<byte>();
        fg.Connect(new VectorSource<byte>(bits), new FskModulator(8, 1.0), new AwgnChannel(0.1f, 3), new FskDemodulator(8, 1.0), sink);
        fg.Run();
        var rx = sink.Data;
        var best = 0.0;
        for (var lag = 0; lag < 20; lag++)
        {
            var n = Math.Min(bits.Length - 100, rx.Length - lag - 100);
            var ok = 0;
            for (var i = 100; i < n; i++) if (rx[i + lag] == bits[i]) ok++;
            best = Math.Max(best, ok / (double)(n - 100));
        }
        Assert.True(best > 0.99, $"bit agreement {best:P2}");
    }

    [Fact]
    public void FrequencyPulsesHaveUnitSymbolArea()
    {
        // Each symbol's frequency pulse integrates to one symbol of full deviation, whatever the BT.
        Assert.Equal(8.0, FskModulator.PulseTaps(8, 0.3).Sum(), 3);
        Assert.Equal(8.0, FskModulator.PulseTaps(8, 0).Sum(), 3);
    }

    [Fact]
    public void OfdmRoundTripsWithFrequencyOffsetNoiseAndMultipath()
    {
        var p = new OfdmParameters();
        var data = RandomBytes(p.BytesPerFrame * 8, 21);
        var fg = new Flowgraph();
        var rx = new OfdmDemodulator(p);
        var sink = new VectorSink<byte>();
        var taps = new[] { new Complex32(1, 0), new Complex32(0.3f, 0.2f), new Complex32(0, -0.1f) };
        fg.Connect(new VectorSource<byte>(data), new OfdmModulator(p), new Delay<Complex32>(137),
            new ChannelModel(0.03f, 0.3 / p.FftSize, 1.0, taps, seed: 2), rx, sink);
        fg.Run();
        var got = sink.Data;
        Assert.True(rx.FramesDecoded >= 7, $"decoded {rx.FramesDecoded} frames");
        Assert.Equal(0.3, rx.LastFrequencyOffset, 1);
        var frames = got.Length / p.BytesPerFrame;
        var errors = Bits.HammingDistance(Bits.Unpack(data.AsSpan(0, frames * p.BytesPerFrame)), Bits.Unpack(got.AsSpan(0, frames * p.BytesPerFrame)));
        Assert.True(errors < frames * p.BitsPerFrame * 0.001, $"{errors} bit errors");
    }

    [Fact]
    public void OfdmTimingStaysExactWhenSamplesTrickleIn()
    {
        // A throttle delivers small, irregular chunks, so frames are often seen before they are complete.
        var p = new OfdmParameters();
        var payload = RandomBytes(p.BytesPerFrame, 12);
        var fg = new Flowgraph();
        var sink = new VectorSink<byte>();
        fg.Connect(new VectorSource<byte>(payload, repeat: true), new Head<byte>(p.BytesPerFrame * 150), new OfdmModulator(p),
            new Throttle<Complex32>(2e6), new ChannelModel(0.02f, 0.1 / p.FftSize, 1.0, [new(1, 0), new(0.3f, 0.2f)], seed: 4),
            new OfdmDemodulator(p), sink);
        fg.Run();
        var got = sink.Data;
        var frames = got.Length / p.BytesPerFrame;
        Assert.True(frames >= 140, $"{frames} frames");
        var bad = Enumerable.Range(0, frames).Count(f => !got.AsSpan(f * p.BytesPerFrame, p.BytesPerFrame).SequenceEqual(payload));
        Assert.Equal(0, bad);
    }

    [Fact]
    public void PacketsSurviveANoisyBpskLink()
    {
        var format = new PacketFormat();
        var framer = new PacketFramer(format, idleFill: false);
        var payloads = Enumerable.Range(0, 5).Select(i => RandomBytes(40 + i, 30 + i)).ToArray();
        foreach (var p in payloads) framer.Enqueue(p);
        var deframer = new PacketDeframer(format);
        var received = new List<byte[]>();
        deframer.PacketReceived += received.Add;
        var fg = new Flowgraph();
        var head = new Head<byte>(payloads.Sum(p => format.Frame(p).Length));
        fg.Connect(framer, head, new BitsToNrz(), new FloatToComplexReal(), new AwgnChannel(0.35f, 1),
            new ComplexToReal(), new BinarySlicer(), deframer);
        fg.Run();
        Assert.True(received.Count >= 4, $"received {received.Count}");
        foreach (var r in received) Assert.Contains(payloads, p => p.SequenceEqual(r));
    }

    [Fact]
    public void AccessCodeCorrelatorTagsTheFollowingBit()
    {
        var code = "1110001011001010";
        var bits = new List<byte>(new byte[50]);
        bits.AddRange(Bits.FromString(code));
        bits.AddRange(new byte[20]);
        var fg = new Flowgraph();
        var corr = new CorrelateAccessCodeTag(code, 0);
        var sink = new VectorSink<byte>();
        fg.Connect(new VectorSource<byte>(bits.ToArray()), corr, sink);
        fg.Run();
        Assert.Single(sink.Tags);
        Assert.Equal(66, sink.Tags[0].Offset);
    }

    [Fact]
    public void CorrelationEstimatorFindsSequence()
    {
        var rng = new FastRandom(2);
        var seq = Enumerable.Range(0, 64).Select(_ => Constellation.Bpsk().Points[rng.Next(2)]).ToArray();
        var signal = new Complex32[2000];
        for (var i = 0; i < signal.Length; i++) signal[i] = rng.NextComplexGaussian() * 0.1f;
        for (var i = 0; i < seq.Length; i++) signal[700 + i] += seq[i] * Complex32.FromPolar(1, 0.7f);
        var fg = new Flowgraph();
        var est = new CorrelationEstimator(seq, 0.6f);
        var sink = new VectorSink<Complex32>();
        fg.Connect(new VectorSource<Complex32>(signal), est, sink);
        fg.Run();
        var tag = Assert.Single(sink.Tags);
        Assert.Equal(700 + est.Delay, tag.Offset);
        Assert.Equal(0.7, tag.Value.DictRef("phase", 0).ToDouble(), 1);
    }

    [Fact]
    public void RayleighFadingHasUnitAveragePower()
    {
        var process = new FadingProcess(8, 0.01, false, 0, 7);
        var power = Enumerable.Range(0, 200_000).Average(_ => process.Next().MagnitudeSquared);
        Assert.InRange(power, 0.85, 1.15);
    }

    [Fact]
    public void RicianFadingIsLessDeepThanRayleigh()
    {
        static double DeepFadeFraction(bool los)
        {
            var p = new FadingProcess(8, 0.01, los, 10, 11);
            return Enumerable.Range(0, 100_000).Count(_ => p.Next().MagnitudeSquared < 0.1) / 100_000.0;
        }
        Assert.True(DeepFadeFraction(true) < DeepFadeFraction(false) / 3);
    }

    [Fact]
    public void SelectiveFadingModelRuns()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<Complex32>();
        fg.Connect(new SignalSource<Complex32>(1e5, Waveform.Constant, 0), new Head<Complex32>(20_000),
            new SelectiveFadingModel([0, 1.5, 3.2], [1, 0.5, 0.25], fdTs: 0.001, seed: 4), sink);
        fg.Run();
        Assert.Equal(20_000, sink.Count);
        Assert.InRange(sink.Data.Average(c => c.MagnitudeSquared), 0.3, 3);
    }

    [Fact]
    public void DopplerShiftMatchesTheory()
    {
        var shift = new DopplerShift(1e6, 437e6, 7500);
        Assert.Equal(437e6 * 7500 / 299_792_458.0, shift.CurrentShiftHz, 3);
    }
}
