using System.Net;
using System.Net.Sockets;
using GnuRadio.Blocks;
using GnuRadio.Grc;
using GnuRadio.Hardware;
using GnuRadio.Sinks;
using GnuRadio.ZeroMQ;

namespace GnuRadio.Tests;

public class PackageTests
{
    private static string GrcPath => Path.Combine(AppContext.BaseDirectory, "grc", "tone_filter.grc");

    [Theory]
    [InlineData("48000", 48000L)]
    [InlineData("samp_rate / 16", 3000.0)]
    [InlineData("int(samp_rate // 4)", 12000L)]
    [InlineData("2**10 + 3 % 2", 1025L)]
    [InlineData("math.pi * 2", 2 * Math.PI)]
    [InlineData("100e3 if samp_rate > 1000 else 5", 100e3)]
    [InlineData("-samp_rate", -48000L)]
    public void GrcExpressionsEvaluate(string text, object expected)
    {
        var scope = new Dictionary<string, object?> { ["samp_rate"] = 48000L };
        var v = GrcExpression.Parse(text).Evaluate(scope);
        Assert.Equal(Convert.ToDouble(expected), Convert.ToDouble(v), 6);
    }

    [Fact]
    public void GrcExpressionsParseListsAndComplex()
    {
        var list = (object?[])GrcExpression.Parse("[1, 2.5, 3]").Evaluate(new Dictionary<string, object?>())!;
        Assert.Equal(3, list.Length);
        var c = (Complex32)GrcExpression.Parse("1+2j").Evaluate(new Dictionary<string, object?>())!;
        Assert.Equal(new Complex32(1, 2), c);
    }

    [Fact]
    public void GrcFileImportsAndRuns()
    {
        var grc = GrcFlowgraph.Load(GrcPath);
        Assert.Equal("Tone filter demo", grc.Document.Title);
        Assert.Equal(3000.0, Convert.ToDouble(grc.Variables["cutoff"]));
        Assert.Contains(grc.Warnings, w => w.Contains("epy_block"));
        Assert.DoesNotContain("qtgui_time_sink_x_0", grc.Blocks.Keys);       // disabled
        Assert.DoesNotContain("blocks_multiply_const_vxx_0", grc.Blocks.Keys); // bypassed
        grc.Flowgraph.Run();
        var data = grc.Block<VectorSink<float>>("blocks_vector_sink_x_0").Data;
        Assert.Equal(12_000, data.Length);
        // The 6 kHz tone is filtered out and the bypassed ×10 gain is gone: the peak is the 1 kHz tone (~1).
        Assert.InRange(data.Skip(1000).Max(), 0.9, 1.1);
    }

    [Fact]
    public void GrcOverridesReplaceVariables()
    {
        var grc = GrcFlowgraph.Load(GrcPath, new Dictionary<string, object?> { ["samp_rate"] = 8000L });
        Assert.Equal(500.0, Convert.ToDouble(grc.Variables["cutoff"]));
    }

    [Fact]
    public void GrcCodeGeneratorEmitsAProgram()
    {
        var code = GrcCodeGenerator.Generate(GrcDocument.Load(GrcPath));
        Assert.Contains("var samp_rate = 48000;", code);
        Assert.Contains("new FirFilterFff(", code);
        Assert.Contains("fg.Connect(blocks_add_xx_0, 0, low_pass_filter_0, 0);", code);
        Assert.Contains("// TODO: 'epy_block'", code);
        Assert.DoesNotContain("blocks_multiply_const_vxx_0", code);
        Assert.Contains("fg.Run();", code);
    }

    [Fact]
    public void PmtSerializationRoundTrips()
    {
        var pdu = Pmt.Pdu(Pmt.Dict(("freq", 433.92e6), ("name", "beacon"), ("count", 7L)), Pmt.Vector(new byte[] { 1, 2, 3, 250 }));
        var back = PmtSerializer.Deserialize(PmtSerializer.Serialize(pdu));
        Assert.Equal(pdu, back);
        foreach (var v in new[] { Pmt.True, Pmt.Nil, Pmt.From(-42L), Pmt.From(1.5), Pmt.From(new Complex32(1, -2)), Pmt.Tuple(1, "a"),
                     Pmt.Vector(new[] { 1f, -2f }), Pmt.Vector(new[] { new Complex32(1, 2) }) })
            Assert.Equal(v, PmtSerializer.Deserialize(PmtSerializer.Serialize(v)));
    }

    [Fact]
    public void PmtSerializationMatchesGnuRadioBytes()
    {
        // pmt.serialize_str(pmt.intern("hi")) == b'\x02\x00\x02hi' and pmt.from_long(1) is int64-tagged.
        Assert.Equal(new byte[] { 0x02, 0x00, 0x02, (byte)'h', (byte)'i' }, PmtSerializer.Serialize(Pmt.Symbol("hi")));
        Assert.Equal(new byte[] { 0x0D, 0, 0, 0, 0, 0, 0, 0, 1 }, PmtSerializer.Serialize(Pmt.From(1L)));
    }

    [Fact]
    public void ZeroMqPubSubCarriesSamples()
    {
        var address = $"tcp://127.0.0.1:{FreePort()}";
        var rx = new Flowgraph();
        var sink = new VectorSink<float>();
        rx.Connect(new ZmqSubSource<float>(address), sink);
        var tx = new Flowgraph();
        tx.Connect(new SignalSource<float>(1000, Waveform.Sawtooth, 1), new Throttle<float>(200_000), new ZmqPubSink<float>(address));
        tx.Start();
        rx.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sink.Count < 20_000 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        rx.Stop();
        tx.Stop();
        Assert.True(sink.Count >= 20_000, $"received {sink.Count}");
        var d = sink.Data;
        // A sawtooth at 1 Hz, 1 kS/s: consecutive samples differ by 0.001 except at the wrap.
        var steps = Enumerable.Range(1, 5000).Count(i => Math.Abs(d[i] - d[i - 1] - 0.001f) < 1e-4f);
        Assert.True(steps > 4900, $"contiguous steps {steps}");
    }

    [Fact]
    public void ZeroMqPushPullCarriesMessagesAndStreams()
    {
        var address = $"tcp://127.0.0.1:{FreePort()}";
        var data = Enumerable.Range(0, 50_000).Select(i => (short)i).ToArray();
        var rx = new Flowgraph();
        var sink = new VectorSink<short>();
        rx.Connect(new ZmqPullSource<short>(address), sink);
        rx.Start();
        var tx = new Flowgraph();
        tx.Connect(new VectorSource<short>(data), new ZmqPushSink<short>(address));
        tx.Run();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sink.Count < data.Length && DateTime.UtcNow < deadline) Thread.Sleep(20);
        rx.Stop();
        Assert.Equal(data, sink.Data);
    }

    [Fact]
    public void SimulatedSdrShowsStationsAtTheirFrequencies()
    {
        var device = new SimulatedSdrDevice(seed: 1) { CenterFrequency = 100e6, SampleRate = 2.4e6, RealTime = false };
        var fg = new Flowgraph();
        var freq = new FrequencySink(1024, device.SampleRate, device.CenterFrequency) { UpdateRate = 1e6 };
        fg.Connect(new SdrSource(device), new Head<Complex32>(200_000), freq);
        fg.Run();
        long v = 0;
        var spectrum = new float[1024];
        Assert.True(freq.TryCopy(ref v, spectrum));
        // 99.9 MHz FM (-35 dBFS) sits 100 kHz below centre; the noise floor is near -90 dB per bin.
        var bin = 512 + (int)Math.Round(-100e3 / device.SampleRate * 1024);
        var stationPeak = spectrum.Skip(bin - 40).Take(80).Max();
        var floor = Fft.Spectral.NoiseFloor(spectrum);
        Assert.True(stationPeak - floor > 25, $"station {stationPeak:F1} dB vs floor {floor:F1} dB");
    }

    [Fact]
    public void SoapySdrReportsAvailabilityWithoutThrowing()
    {
        _ = SoapySdrDevice.IsAvailable;
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
