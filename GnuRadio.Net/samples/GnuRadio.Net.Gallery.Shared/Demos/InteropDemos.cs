using System.Net;
using System.Net.Sockets;
using GnuRadio.Blocks;
using GnuRadio.Grc;
using GnuRadio.Sinks;
using GnuRadio.ZeroMQ;

namespace GnuRadio.Gallery.Demos;

/// <summary>A transmitter and receiver joined only by ZeroMQ, the way GNU Radio flowgraphs are distributed.</summary>
public sealed class ZeroMqBridgeDemo : Demo
{
    private const double Rate = 200e3;
    private SignalSource<Complex32>? _tone;
    private ZmqPubSink<Complex32>? _pub;
    private ZmqSubSource<Complex32>? _sub;
    private double _toneHz = 25e3;
    private string _address = "";

    public ZeroMqBridgeDemo()
    {
        AddParameter(new NumberParameter("tone", new("Transmitter tone", "Nada pemancar"), -90e3, 90e3, 1e3, _toneHz, v =>
        {
            _toneHz = v;
            if (_tone is not null) _tone.Frequency = _toneHz;
        }) { Format = v => Axis.FormatFrequency(v, 0) });
    }

    public override string Id => "zeromq";
    public override DemoCategory Category => DemoCategory.Interop;
    public override Text Title => new("ZeroMQ bridge to GNU Radio", "Jembatan ZeroMQ ke GNU Radio");

    public override Text Summary => new(
        "A PUB sink streams raw complex samples on a TCP endpoint; a SUB source receives them. The bytes on the wire are exactly what GNU Radio's zeromq blocks send, so either side can be a GNU Radio flowgraph.",
        "PUB sink mengalirkan sampel kompleks mentah pada endpoint TCP; SUB source menerimanya. Byte pada kabel sama persis dengan yang dikirim blok zeromq GNU Radio, sehingga sisi mana pun bisa berupa flowgraph GNU Radio.");

    public override Text? Notes => new(
        "In GNU Radio Companion add a ZMQ SUB Source (type complex, address tcp://<this machine>:port) and it will show this transmitter. Message ports use the PMT wire format (ZmqPubMessageSink / ZmqSubMessageSource).",
        "Di GNU Radio Companion tambahkan ZMQ SUB Source (tipe complex, alamat tcp://<mesin ini>:port) dan pemancar ini akan tampil. Port pesan memakai format PMT (ZmqPubMessageSink / ZmqSubMessageSource).");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.ZeroMQ;

        // Machine A: transmitter
        var tx = new Flowgraph("tx");
        tx.Connect(new SignalSource<Complex32>(200e3, Waveform.Cosine, 25e3),
                   new Throttle<Complex32>(200e3),
                   new ZmqPubSink<Complex32>("tcp://*:5555"));
        tx.Start();

        // Machine B (or GNU Radio's "ZMQ SUB Source"): receiver
        var rx = new Flowgraph("rx");
        rx.Connect(new ZmqSubSource<Complex32>("tcp://machine-a:5555"),
                   new GnuRadio.Sinks.FrequencySink(1024, 200e3));
        rx.Start();
        """;

    protected override Flowgraph Build()
    {
        _address = $"tcp://127.0.0.1:{FreePort()}";
        _tone = new SignalSource<Complex32>(Rate, Waveform.Cosine, _toneHz, 0.7f);
        var noise = new NoiseSource<Complex32>(NoiseType.Gaussian, 0.02f, 2);
        var add = new Add<Complex32>();
        _pub = new ZmqPubSink<Complex32>(_address);
        _sub = new ZmqSubSource<Complex32>(_address);
        var spectrum = new FrequencySink(1024, Rate) { Averaging = 0.3f, UpdateRate = 20 };
        var scope = new TimeSink<Complex32>(400, Rate) { UpdateRate = 20 };

        // One Flowgraph object, two independent graphs: only ZeroMQ links them.
        var fg = new Flowgraph("zmq_bridge");
        fg.Connect(_tone, 0, add, 0);
        fg.Connect(noise, 0, add, 1);
        fg.Connect(add, new Throttle<Complex32>(Rate), _pub);
        fg.Connect(_sub, 0, spectrum, 0);
        fg.Connect(_sub, 0, scope, 0);

        AddView(new SpectrumPanel(new("Received over ZeroMQ", "Diterima lewat ZeroMQ"), spectrum, -80, 0, () => [(_toneHz, "tone")], Size: PanelSize.Half, Height: 240));
        AddView(new ScopePanel(new("I/Q after the socket", "I/Q setelah soket"), scope, -1, 1, Height: 240));
        AddReadout(new("Endpoint", "Endpoint"), () => _address);
        AddReadout(new("Messages sent", "Pesan terkirim"), () => _pub.MessagesSent.ToString("N0"));
        AddReadout(new("Samples received", "Sampel diterima"), () => _sub.Statistics.ProducedItems.ToString("N0"));
        return fg;
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

/// <summary>Opens a GNU Radio Companion file, runs it, and shows the generated C#.</summary>
public sealed class GrcImportDemo : Demo
{
    private GrcFlowgraph? _grc;
    private string _code = "";

    public override string Id => "grc-import";
    public override DemoCategory Category => DemoCategory.Interop;
    public override Text Title => new("Run a .grc flowgraph", "Menjalankan flowgraph .grc");

    public override Text Summary => new(
        "Loads a flowgraph saved by GNU Radio Companion 3.10, evaluates its Python parameter expressions, maps the blocks onto GnuRadio.Net, runs it, and emits the equivalent C# program.",
        "Memuat flowgraph yang disimpan GNU Radio Companion 3.10, mengevaluasi ekspresi parameter Python-nya, memetakan blok ke GnuRadio.Net, menjalankannya, dan menghasilkan program C# yang setara.");

    public override string Code => """
        using GnuRadio.Grc;

        var grc = GrcFlowgraph.Load("live_spectrum.grc",
            overrides: new Dictionary<string, object?> { ["tone"] = 12_000.0 });
        foreach (var warning in grc.Warnings) Console.WriteLine(warning);

        var spectrum = grc.Block<GnuRadio.Sinks.FrequencySink>("qtgui_freq_sink_x_0");
        grc.Flowgraph.Start();

        // Or generate a standalone C# program:
        File.WriteAllText("LiveSpectrum.cs", GrcCodeGenerator.Generate(grc.Document, runForever: true));
        """;

    public static string ReadEmbedded(string name)
    {
        using var stream = typeof(GrcImportDemo).Assembly.GetManifestResourceStream($"grc/{name}")
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    protected override Flowgraph Build()
    {
        var document = GrcDocument.Parse(ReadEmbedded("live_spectrum.grc"));
        _grc = GrcImporter.Import(document);
        _code = GrcCodeGenerator.Generate(document, runForever: true);
        foreach (var sink in _grc.Blocks.Values.OfType<FrequencySink>())
            AddView(new SpectrumPanel(Text.Same(sink.Title), sink, -100, 0, Size: PanelSize.Half, Height: 220));
        foreach (var sink in _grc.Blocks.Values.OfType<TimeSink<Complex32>>())
            AddView(new ScopePanel(Text.Same(sink.Title), sink, -1.2f, 1.2f, Height: 220));
        AddView(new FlowgraphPanel(new("Imported flowgraph", "Flowgraph yang diimpor"), () => _grc.Flowgraph));
        AddView(new TextPanel(new("Generated C#", "C# yang dihasilkan"), () => _code, PanelSize.Full, 360));
        AddReadout(new("File", "Berkas"), () => $"{document.Title} ({document.Blocks.Count} blocks)");
        AddReadout(new("Variables", "Variabel"), () => string.Join(", ", _grc.Variables.Select(v => $"{v.Key}={v.Value}")));
        AddReadout(new("Warnings", "Peringatan"), () => _grc.Warnings.Count == 0 ? "none / tidak ada" : string.Join("; ", _grc.Warnings));
        return _grc.Flowgraph;
    }
}
