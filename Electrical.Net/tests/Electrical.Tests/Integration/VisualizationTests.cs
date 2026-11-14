using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Xml.Linq;
using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.Visualization;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class VisualizationTests
{
    private static XDocument ParseSvg(string svg)
    {
        var doc = XDocument.Parse(svg);
        Assert.Equal("svg", doc.Root!.Name.LocalName);
        return doc;
    }

    [Theory]
    [InlineData("case9")]
    [InlineData("case14")]
    [InlineData("case33bw")]
    [InlineData("synthetic60")]
    public void SldLayout_PlacesEveryBus_SlackOnTop_AndReducesCrossings(string caseName)
    {
        var net = TestCases.ByName(caseName);
        var layout = SldLayout.Compute(net);
        Assert.Equal(net.BusCount, layout.Nodes.Count);
        Assert.Equal(net.Branches.Count, layout.Edges.Count);
        Assert.Equal(0, layout.Node(net.SlackBus!.Id).Layer);
        Assert.True(layout.Crossings <= layout.InitialCrossings);
        // bars in one layer never overlap and keep their order
        foreach (var layer in layout.Nodes.GroupBy(n => n.Layer))
        {
            var ordered = layer.OrderBy(n => n.Order).ToList();
            for (int i = 1; i < ordered.Count; i++) Assert.True(ordered[i].Left >= ordered[i - 1].Right - 1e-6);
        }
        // BFS layering: every branch spans at most one layer and its route ends on both bars
        foreach (var e in layout.Edges)
        {
            var a = layout.Node(e.FromBus); var b = layout.Node(e.ToBus);
            Assert.True(Math.Abs(a.Layer - b.Layer) <= 1);
            Assert.Equal(a.Y, e.Path[0].Y, 6);
            Assert.Equal(b.Y, e.Path[^1].Y, 6);
            Assert.InRange(e.Path[0].X, a.Left, a.Right);
            Assert.InRange(e.Path[^1].X, b.Left, b.Right);
        }
    }

    [Fact]
    public void SldLayout_IsDeterministic()
    {
        var a = SldLayout.Compute(TestCases.SyntheticGrid(80));
        var b = SldLayout.Compute(TestCases.SyntheticGrid(80));
        Assert.Equal(a.Nodes.Select(n => (n.BusId, n.X, n.Y)), b.Nodes.Select(n => (n.BusId, n.X, n.Y)));
        Assert.Equal(SldRenderer.Render(TestCases.Case14()), SldRenderer.Render(TestCases.Case14()));
    }

    [Fact]
    public void SldRender_WithPowerFlowOverlay_IsValidSvgWithSymbolsAndFlows()
    {
        var net = TestCases.Case14();
        var pf = PowerFlowSolver.Solve(net);
        var svg = SldRenderer.Render(net, pf, new SldOptions { Title = "IEEE 14-bus" });
        var doc = ParseSvg(svg);
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(14, doc.Descendants(ns + "g").Count(g => (string?)g.Attribute("class") == "eln-bus"));
        Assert.Equal(net.Generators.Count, doc.Descendants(ns + "g").Count(g => (string?)g.Attribute("class") == "eln-gen"));
        Assert.Equal(net.Loads.Count, doc.Descendants(ns + "g").Count(g => (string?)g.Attribute("class") == "eln-load"));
        // each transformer is drawn as two overlapping circles inside its branch group
        int trafos = net.Branches.Count(b => b.IsTransformer);
        Assert.True(trafos > 0);
        Assert.Equal(2 * trafos, doc.Descendants(ns + "g").Where(g => (string?)g.Attribute("class") == "eln-branch")
            .SelectMany(g => g.Elements(ns + "circle")).Count());
        Assert.Contains("1.060 pu", svg); // slack voltage label
        Assert.Contains(" MW</text>", svg);
        Assert.Contains("var(--eln-fg", svg);
        // voltage color of bus 1 (1.06 pu) is on the high side of the ramp
        Assert.Contains(ColorRamp.Voltage().Hex(1.06), svg);
    }

    [Fact]
    public void ColorRamps_VoltageCenteredAtNominal_LoadingGreenAmberRed()
    {
        var v = ColorRamp.Voltage();
        Assert.Equal("#2bb673", v.Hex(1.0));
        Assert.Equal("#2563eb", v.Hex(0.85)); // clamped low
        Assert.Equal("#e5484d", v.Hex(1.2));  // clamped high
        Assert.Equal("#2bb673", ColorRamps.LoadingHex(0));
        Assert.Equal("#f5a524", ColorRamps.LoadingHex(70));
        Assert.Equal("#e5484d", ColorRamps.LoadingHex(100));
        Assert.Equal(new Rgba(128, 128, 128), Rgba.Lerp(new Rgba(0, 0, 0), new Rgba(255, 255, 255), 0.5));
        Assert.Equal(new Rgba(0x12, 0x34, 0x56), Rgba.Parse("#123456"));
    }

    [Fact]
    public void SvgTheme_Resolve_ReplacesAllVariables()
    {
        var svg = SvgCharts.Line([new ChartSeries("P", [1, 2, 3, 2]), new ChartSeries("Q", [0.5, 0.7, 0.2, 0.1])], new ChartOptions { Title = "Flows" });
        Assert.Contains("var(--eln-", svg);
        var light = SvgTheme.Resolve(svg, dark: false);
        var dark = SvgTheme.Resolve(svg, dark: true);
        Assert.DoesNotContain("var(", light);
        Assert.DoesNotContain("var(", dark);
        Assert.Contains(SvgTheme.Literal("eln-series-1", false), light);
        Assert.Contains(SvgTheme.Literal("eln-series-1", true), dark);
        ParseSvg(light);
        Assert.Equal("#abc", SvgTheme.Resolve("var(--unknown, #abc)", false));
        Assert.Contains("--eln-accent", SvgTheme.Stylesheet);
        Assert.Contains("prefers-color-scheme: dark", SvgTheme.Stylesheet);
    }

    [Fact]
    public void Charts_AllProduceWellFormedSvg()
    {
        var ts = new TimeSeries(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(15),
            Enumerable.Range(0, 96).Select(i => 50 + 20 * Math.Sin(i * Math.PI / 48)).ToArray(), "Load", "MW");
        var heat = new double[7, 24];
        for (int d = 0; d < 7; d++) for (int h = 0; h < 24; h++) heat[d, h] = 40 + 30 * Math.Sin(h / 24.0 * Math.PI) + d;
        string[] svgs =
        [
            SvgCharts.TimeSeries([ts], new ChartOptions { Title = "Daily load", ReferenceLines = [(65, "Limit")] }),
            SvgCharts.Bar(["A", "B", "C"], [new ChartSeries("2025", [3, -1, 4]), new ChartSeries("2026", [2, 5, 1])]),
            SvgCharts.Bar(["A", "B"], [new ChartSeries("x", [3, 4]), new ChartSeries("y", [1, 2])], stacked: true),
            SvgCharts.StackedArea([new ChartSeries("Solar", [0, 2, 5, 2, 0]), new ChartSeries("Wind", [3, 3, 2, 4, 5])]),
            SvgCharts.Heatmap(heat, ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], Enumerable.Range(0, 24).Select(h => h.ToString()).ToList(), new HeatmapOptions { Unit = "%", ShowValues = true }),
            SvgCharts.Phasor([new PhasorItem("Va", 1, 0), new PhasorItem("Vb", 1, -120), new PhasorItem("Vc", 1, 120),
                PhasorItem.FromComplex("Ia", Complex.FromPolarCoordinates(0.8, -0.5), PhasorKind.Current)], "Phasors"),
            SvgCharts.Polar([new PolarSeries("Capability", [(0, 1), (90, 0.8), (180, 0.4), (270, 0.8)]) { Closed = true, Fill = true }], "Polar"),
            SvgCharts.Tcc([TccCurve.FromFunction("IEC SI", i => 0.14 * 0.1 / (Math.Pow(i / 400, 0.02) - 1), 420, 20000)],
                [new TccMarker(8000, "Ik max")], new TccOptions { Title = "Coordination" }),
            SvgCharts.Gauge(0.97, 0.9, 1.1, "Bus 5", "pu", [new GaugeBand(0.9, 0.95, "var(--eln-danger, #d62f3a)"), new GaugeBand(0.95, 1.05, "var(--eln-ok, #1f9d5f)")]),
            SvgCharts.KpiTile("Losses", "13.39", "MW", [14, 13.8, 13.5, 13.39], "-2.1 %", true),
            SvgCharts.Sparkline([1, 3, 2, 5, 4]),
        ];
        foreach (var s in svgs) ParseSvg(s);
        Assert.Contains("00:00", svgs[0]);
        Assert.Contains("Limit", svgs[0]);
    }

    [Fact]
    public void Tcc_UsesLogDecadeAxes()
    {
        var curve = new TccCurve("Fuse", [(100, 100), (1000, 1), (10000, 0.01)]);
        var svg = SvgCharts.Tcc([curve]);
        var doc = ParseSvg(svg);
        var texts = doc.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToList();
        Assert.Contains("100", texts);
        Assert.Contains("1k", texts);
        Assert.Contains("10k", texts);
        Assert.Contains("0.01", texts);
        // points equally spaced in log-log space -> collinear polyline
        var poly = doc.Descendants().First(e => e.Name.LocalName == "polyline").Attribute("points")!.Value
            .Split(' ').Select(p => p.Split(',').Select(double.Parse).ToArray()).ToArray();
        Assert.Equal(poly[1][0] - poly[0][0], poly[2][0] - poly[1][0], 1);
        Assert.Equal(poly[1][1] - poly[0][1], poly[2][1] - poly[1][1], 1);
    }

    [Fact]
    public void Png_EncodesValidChunksAndDecodablePixels()
    {
        var raster = new Raster(4, 3).Fill((x, y) => x + 4 * y);
        var png = raster.ToPng(ColorRamp.Viridis, scale: 2);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(png.Length - 8, png.AsSpan().LastIndexOf("IEND"u8)); // IEND type followed only by its CRC
        // walk chunks, verify CRCs, collect IDAT
        int pos = 8; using var idat = new MemoryStream(); int w = 0, h = 0;
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = png.AsSpan(pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(PngEncoder.Crc32(data, PngEncoder.Crc32(type)), crc);
            if (type.SequenceEqual("IHDR"u8)) { w = BinaryPrimitives.ReadInt32BigEndian(data); h = BinaryPrimitives.ReadInt32BigEndian(data[4..]); }
            if (type.SequenceEqual("IDAT"u8)) idat.Write(data);
            pos += 12 + len;
        }
        Assert.Equal((8, 6), (w, h));
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream(); z.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal(h * (1 + w * 4), bytes.Length);
        // undo the Sub filter on the first row and check pixel 0 = ramp min color
        Assert.Equal(1, bytes[0]);
        var first = ColorRamp.Viridis.Map(0);
        Assert.Equal((first.R, first.G, first.B), (bytes[1], bytes[2], bytes[3]));
        Assert.Equal(0xCBF43926u, PngEncoder.Crc32("123456789"u8)); // standard CRC-32 check value
    }

    [Fact]
    public void NiceRangeAndTimeTicks()
    {
        var (min, max, step) = SvgFormat.NiceRange(0.93, 1.06);
        Assert.True(min <= 0.93 && max >= 1.06);
        Assert.Equal(0.05, step, 9);
        var (ticks, fmt) = SvgFormat.TimeTicks(new DateTimeOffset(2026, 1, 1, 0, 7, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 23, 45, 0, TimeSpan.Zero));
        Assert.Equal("HH:mm", fmt);
        Assert.All(ticks, t => Assert.Equal(0, t.Minute));
        Assert.InRange(ticks.Count, 4, 8);
    }

    [Fact]
    public void AddVisualization_RegistersRenderer()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddVisualization()).BuildServiceProvider();
        var r = sp.GetRequiredService<IDiagramRenderer>();
        var svg = r.Resolve(r.SingleLine(TestCases.Case9()), dark: true);
        Assert.DoesNotContain("var(", svg);
        ParseSvg(svg);
    }
}
