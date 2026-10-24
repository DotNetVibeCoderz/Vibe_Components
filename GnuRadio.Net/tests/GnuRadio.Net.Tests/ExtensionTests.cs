using System.Net;
using System.Net.Sockets;
using System.Text;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Digital;
using GnuRadio.Extensions;
using GnuRadio.Fft;
using GnuRadio.Network;
using GnuRadio.Sinks;

namespace GnuRadio.Tests;

public class ExtensionTests
{
    [Fact]
    public void LoRaRoundTripsSymbolsThroughNoiseAndTimingOffset()
    {
        var p = new LoRaParameters(SpreadingFactor: 7, Oversampling: 2);
        var rng = new FastRandom(1);
        var symbols = Enumerable.Range(0, 16 * 4).Select(_ => (byte)rng.Next(128)).ToArray();
        var fg = new Flowgraph();
        var demod = new LoRaDemodulator(p);
        var sink = new VectorSink<byte>();
        fg.Connect(new VectorSource<byte>(symbols), new LoRaModulator(p, 16), new Delay<Complex32>(333),
            new AwgnChannel(0.5f, 3), demod, sink);
        fg.Run();
        Assert.True(demod.FramesDetected >= 3, $"frames {demod.FramesDetected}");
        var got = sink.Data;
        var matches = 0;
        for (var f = 0; f + 16 <= got.Length; f += 16)
            for (var frame = 0; frame < 4; frame++)
                if (got.AsSpan(f, 16).SequenceEqual(symbols.AsSpan(frame * 16, 16))) matches++;
        Assert.True(matches >= 3, $"matching frames {matches}");
    }

    [Fact]
    public void FmcwRadarFindsTargetRanges()
    {
        var p = new FmcwParameters();
        var targets = new[] { new RadarTarget(40, 0), new RadarTarget(110, 5) };
        var fg = new Flowgraph();
        var processor = new FmcwRangeProcessor(p);
        var sink = new VectorSink<float>(processor.FftSize / 2);
        fg.Connect(new FmcwRadarSource(p, targets, 0.01f, 3), new Head<Complex32>(p.SamplesPerChirp * 4), processor, sink);
        fg.Run();
        var profile = sink.Data.AsSpan(0, processor.FftSize / 2);
        var hits = Cfar.Detect(profile).Select(processor.BinRange).ToArray();
        Assert.Contains(hits, r => Math.Abs(r - 40) < 2 * p.RangeResolution);
        Assert.Contains(hits, r => Math.Abs(r - 110) < 2 * p.RangeResolution);
    }

    [Fact]
    public void HdlcAx25RoundTrip()
    {
        var frame = Ax25.UiFrame("CQ", "YB0GRC", Encoding.ASCII.GetBytes("Hello from GnuRadio.Net"));
        var bits = Hdlc.NrziEncode(Hdlc.Frame(frame));
        var fg = new Flowgraph();
        var deframer = new HdlcDeframer();
        byte[]? received = null;
        deframer.FrameReceived += f => received = f;
        fg.Connect(new VectorSource<byte>(Hdlc.NrziDecode(bits)), deframer);
        fg.Run();
        Assert.NotNull(received);
        var (dst, src, info) = Ax25.Parse(received);
        Assert.Equal("CQ", dst);
        Assert.Equal("YB0GRC", src);
        Assert.Equal("Hello from GnuRadio.Net", Encoding.ASCII.GetString(info));
    }

    [Fact]
    public void SignalDetectorFindsTwoCarriers()
    {
        var x = new Complex32[32768];
        var rng = new FastRandom(4);
        for (var i = 0; i < x.Length; i++)
            x[i] = Complex32.FromPolar(1, 2 * MathF.PI * 0.1f * i) + Complex32.FromPolar(0.5f, -2 * MathF.PI * 0.3f * i) + rng.NextComplexGaussian() * 0.01f;
        var psd = Spectral.WelchPsd(x, 1e6, 1024);
        var found = SignalDetector.Detect(psd, 1e6, 0, 15, 1);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, s => Math.Abs(s.CenterFrequency - 100e3) < 2e3);
        Assert.Contains(found, s => Math.Abs(s.CenterFrequency + 300e3) < 2e3);
    }

    [Theory]
    [InlineData("bpsk")]
    [InlineData("qpsk")]
    [InlineData("8psk")]
    [InlineData("16qam")]
    public void ModulationClassifierRecognisesConstellations(string name)
    {
        var classifier = ModulationClassifier.CreateDefault();
        var c = Constellation.FromName(name);
        var rng = new FastRandom(99);
        var phase = Complex32.FromPolar(1, 0.3f);
        var block = Enumerable.Range(0, 4096).Select(_ => c.Points[rng.Next(c.Arity)] * phase + rng.NextComplexGaussian() * 0.1f).ToArray();
        var (label, _) = classifier.Classify(block);
        Assert.Equal(c.Name, label);
    }

    [Fact]
    public void TcpLoopbackCarriesSamples()
    {
        var port = FreePort();
        var data = Enumerable.Range(0, 20_000).Select(i => new Complex32(i, -i)).ToArray();
        var rx = new Flowgraph();
        var source = new TcpSource<Complex32>("127.0.0.1", port, TcpMode.Server) { StopOnDisconnect = true };
        var sink = new VectorSink<Complex32>();
        rx.Connect(source, sink);
        rx.Start();
        var tx = new Flowgraph();
        tx.Connect(new VectorSource<Complex32>(data), new TcpSink<Complex32>("127.0.0.1", port, TcpMode.Client));
        tx.Run();
        Assert.True(rx.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(data, sink.Data);
    }

    [Fact]
    public void UdpLoopbackCarriesSamples()
    {
        var port = FreePort();
        var rx = new Flowgraph();
        var sink = new VectorSink<float>();
        rx.Connect(new UdpSource<float>(port, "127.0.0.1"), sink);
        rx.Start();
        Thread.Sleep(100);
        var tx = new Flowgraph();
        var data = Enumerable.Range(0, 3680).Select(i => (float)i).ToArray();
        tx.Connect(new VectorSource<float>(data), new UdpSink<float>("127.0.0.1", port, 368 * 4));
        tx.Run();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sink.Count < data.Length && DateTime.UtcNow < deadline) Thread.Sleep(20);
        rx.Stop();
        Assert.Equal(data, sink.Data);
    }

    [Fact]
    public void FileSinkAndSourceRoundTripComplexSamples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gr-{Guid.NewGuid():N}.cfile");
        try
        {
            var data = Enumerable.Range(0, 10_000).Select(i => new Complex32(i * 0.5f, 1)).ToArray();
            var fg = new Flowgraph();
            fg.Connect(new VectorSource<Complex32>(data), new FileSink<Complex32>(path));
            fg.Run();
            Assert.Equal(data.Length * 8, new FileInfo(path).Length);
            var back = new Flowgraph();
            var sink = new VectorSink<Complex32>();
            back.Connect(new FileSource<Complex32>(path), sink);
            back.Run();
            Assert.Equal(data, sink.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WavFilesRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gr-{Guid.NewGuid():N}.wav");
        try
        {
            var fg = new Flowgraph();
            fg.Connect(new SignalSource<float>(8000, Waveform.Sine, 440, 0.5f), new Head<float>(8000), new WavFileSink(path, 1, 8000));
            fg.Run();
            var src = new WavFileSource(path);
            Assert.Equal(8000, src.SampleRate);
            var sink = new VectorSink<float>();
            var back = new Flowgraph();
            back.Connect(src, sink);
            back.Run();
            Assert.Equal(8000, sink.Count);
            Assert.Equal(0.5, sink.Data.Max(), 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VisualSinksPublishFrames()
    {
        var fg = new Flowgraph();
        var freq = new FrequencySink(512, 1e5) { UpdateRate = 1000 };
        var water = new WaterfallSink(256, 1e5, history: 32) { UpdateRate = 1000 };
        var scope = new TimeSink<Complex32>(200, 1e5) { UpdateRate = 1000 };
        var constellation = new ConstellationSink(256);
        var src = new SignalSource<Complex32>(1e5, Waveform.Cosine, 25e3);
        var head = new Head<Complex32>(50_000);
        fg.Connect(src, head);
        fg.Connect(head, 0, freq, 0);
        fg.Connect(head, 0, water, 0);
        fg.Connect(head, 0, scope, 0);
        fg.Connect(head, 0, constellation, 0);
        fg.Run();

        long v = 0;
        var spectrum = new float[512];
        Assert.True(freq.TryCopy(ref v, spectrum));
        Assert.False(freq.TryCopy(ref v, spectrum));
        Assert.Equal(256 + 128, Kernels.Volk.ArgMax(spectrum).Index);

        long cursor = 0;
        var rows = new float[256 * 64];
        Assert.Equal(32, water.ReadNewRows(ref cursor, rows));

        long sv = 0;
        var traces = new[] { new float[200], new float[200] };
        Assert.True(scope.TryCopy(ref sv, traces));
        Assert.Equal(2, scope.TraceCount);

        long cv = 0;
        var points = new Complex32[256];
        Assert.True(constellation.TryCopy(ref cv, points));
        Assert.All(points, p => Assert.Equal(1, p.Magnitude, 2));
    }

    [Fact]
    public void ColormapsAndAxesBehave()
    {
        Assert.Equal(256, Colormap.Phosphor.Rgba.Length);
        Assert.Equal([0.0, 0.5, 1.0], Axis.NiceTicks(0, 1, 2));
        Assert.Equal("100.5 MHz", Axis.FormatFrequency(100.5e6));
    }

    [Fact]
    public void BlockRegistryDiscoversTaggedBlocks()
    {
        BlockRegistry.Register(typeof(ExtensionTests).Assembly);
        Assert.True(BlockRegistry.TryGet("test_doubler", out var d));
        Assert.Equal("Tests", d.Category);
        Assert.IsType<Doubler>(BlockRegistry.Create("test_doubler"));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

[GrBlock("test_doubler", Category = "Tests")]
public sealed class Doubler() : SyncBlock<float, float>("doubler")
{
    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = 2 * input[i];
    }
}
