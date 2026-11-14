using System.Text;
using System.Xml.Linq;
using Electrical.CIM;
using Electrical.PowerFlow;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;

namespace Electrical.Gallery.Shared;

/// <summary>Round-trip comparison line (original vs re-imported).</summary>
public sealed record CimCheck(L Quantity, string Original, string Imported, bool Match);

/// <summary>Size of one CGMES profile document.</summary>
public sealed record CimProfileSize(string Profile, int Objects, int Bytes);

public sealed record CimDemoResult(
    string Case, string Snippet, int TotalLines, int TotalBytes, IReadOnlyList<(string Class, int Count)> Classes, string ClassChartSvg,
    IReadOnlyList<CimProfileSize> Profiles, IReadOnlyList<CimCheck> Checks, IReadOnlyList<string> Warnings, bool RoundTripOk, IReadOnlyList<Kpi> Kpis);

/// <summary>CIM (CGMES-style RDF/XML) export, profile split and import round trip.</summary>
public static class CimScenario
{
    public static readonly string[] Cases = ["case14", "case9", "case33bw", "synthetic"];

    public static CimDemoResult Run(string caseKey = "case14", int snippetLines = 70, string lang = "en")
    {
        var net = GalleryCases.Load(caseKey);
        string rdf = CimExporter.ExportString(net);
        var lines = rdf.Split('\n');
        string snippet = string.Join('\n', lines.Take(snippetLines).Select(l => l.TrimEnd('\r'))) + (lines.Length > snippetLines ? "\n  …" : "");

        var doc = XDocument.Parse(rdf);
        var classes = doc.Root!.Elements().GroupBy(e => e.Name.LocalName).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        var top = classes.Take(12).ToList();
        string chart = SvgCharts.Bar(top.Select(c => c.Key).ToList(),
            [new ChartSeries(lang == "id" ? "objek" : "objects", top.Select(c => (double)c.Item2).ToList(), SvgTheme.Var("eln-accent"))],
            new ChartOptions { Width = 680, Height = 250, YLabel = lang == "id" ? "jumlah objek" : "objects", ShowLegend = false, ValueFormat = "0" });

        var profiles = CimExporter.ExportProfiles(net);
        var sizes = new (string Name, XDocument Doc)[] { ("EQ", profiles.Equipment), ("TP", profiles.Topology), ("SSH", profiles.SteadyStateHypothesis), ("SV", profiles.StateVariables) }
            .Select(p => new CimProfileSize(p.Name, p.Doc.Root!.Elements().Count(), Encoding.UTF8.GetByteCount(p.Doc.ToString()))).ToList();

        var imported = CimImporter.ImportDetailed([doc], new CimImportOptions { BaseMva = net.BaseMva, FrequencyHz = net.FrequencyHz, Name = net.Name });
        var back = imported.Network;
        var pf0 = PowerFlowSolver.Solve(net);
        var pf1 = PowerFlowSolver.Solve(back);
        // Compare voltages by bus id (the importer keeps ids through the extension namespace).
        double dv = 0;
        foreach (var b in pf0.Buses)
        {
            var other = pf1.Buses.FirstOrDefault(x => x.BusId == b.BusId);
            dv = Math.Max(dv, other is null ? double.PositiveInfinity : Math.Abs(other.Vm - b.Vm));
        }
        var checks = new List<CimCheck>
        {
            new(new("Buses", "Bus"), net.BusCount.ToString(), back.BusCount.ToString(), net.BusCount == back.BusCount),
            new(new("Branches", "Cabang"), net.Branches.Count.ToString(), back.Branches.Count.ToString(), net.Branches.Count == back.Branches.Count),
            new(new("Generators", "Generator"), net.Generators.Count.ToString(), back.Generators.Count.ToString(), net.Generators.Count == back.Generators.Count),
            new(new("Load (MW)", "Beban (MW)"), F(net.TotalLoadMw, "0.###"), F(back.TotalLoadMw, "0.###"), Math.Abs(net.TotalLoadMw - back.TotalLoadMw) < 1e-6),
            new(new("Losses (MW)", "Rugi-rugi (MW)"), F(pf0.LossesMw, "0.0000"), F(pf1.LossesMw, "0.0000"), Math.Abs(pf0.LossesMw - pf1.LossesMw) < 1e-4),
            new(new("Max |ΔV| (pu)", "Maks |ΔV| (pu)"), "—", dv.ToString("0.0E+0", System.Globalization.CultureInfo.InvariantCulture), dv < 1e-6),
        };
        bool ok = checks.All(c => c.Match);
        int bytes = Encoding.UTF8.GetByteCount(rdf);
        var kpis = new List<Kpi>
        {
            new(new("CIM objects", "Objek CIM"), doc.Root.Elements().Count().ToString(), null, new($"{classes.Count} classes", $"{classes.Count} kelas")),
            new(new("Document", "Dokumen"), F(bytes / 1024.0, "0.0"), "KiB", new($"{lines.Length:N0} lines RDF/XML", $"{lines.Length:N0} baris RDF/XML")),
            new(new("Profiles", "Profil"), "EQ · TP · SSH · SV", null, new($"{sizes.Sum(s => s.Objects)} objects split", $"{sizes.Sum(s => s.Objects)} objek terpisah")),
            new(new("Round trip", "Bolak-balik"), ok ? "IDENTICAL" : "DIFFERS", null, new($"max |ΔV| {dv:0.0E+0} pu", $"maks |ΔV| {dv:0.0E+0} pu"), ok ? "ok" : "alarm"),
        };
        return new CimDemoResult(caseKey, snippet, lines.Length, bytes, classes, chart, sizes, checks, imported.Warnings, ok, kpis);
    }
}
