using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Electrical.CIM;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// CGMES 3.0: exports a test system as EQ / TP / SSH / SV profiles (sizes, classes, a snippet), validates the clean set with the 22
/// <see cref="CgmesValidator"/> rules, breaks one thing on purpose and shows what the validator reports, and proves the round trip with a power flow.
/// </summary>
public sealed class CgmesDemo : IInteractiveScenario
{
    public string Id => "cgmes";

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Cim = Cgmes3Namespaces.Cim;
    private static readonly XNamespace Md = Cgmes3Namespaces.Md;

    private static readonly (string Key, L Text, L Explain)[] Breaks =
    [
        ("ref", new("dangling reference", "referensi menggantung"), new("A Terminal's ConductingEquipment points at an mRID that is not in the set.", "ConductingEquipment milik sebuah Terminal menunjuk mRID yang tidak ada di set.")),
        ("mand", new("missing attribute", "atribut hilang"), new("ACLineSegment.r is removed from the first line (mandatory in EQ).", "ACLineSegment.r dihapus dari saluran pertama (wajib di EQ).")),
        ("param", new("negative reactance", "reaktansi negatif"), new("ACLineSegment.x of the first line is set to −0.5 Ω.", "ACLineSegment.x saluran pertama diatur −0,5 Ω.")),
        ("kcl", new("SV mismatch", "selisih SV"), new("25 MW are added to one SvPowerFlow.p, so the node no longer balances.", "25 MW ditambahkan ke satu SvPowerFlow.p, sehingga simpul tidak lagi seimbang.")),
        ("header", new("broken header", "header rusak"), new("Model.scenarioTime is removed from the TP header.", "Model.scenarioTime dihapus dari header TP.")),
        ("dup", new("duplicate mRID", "mRID ganda"), new("The second line gets the rdf:ID of the first one.", "Saluran kedua diberi rdf:ID saluran pertama.")),
    ];

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new ChoiceParam("case", new("Network", "Jaringan"), [("case9", Same("WSCC 9")), ("case14", Same("IEEE 14")), ("case33bw", Same("Baran–Wu 33"))], "case14"),
        new ChoiceParam("style", new("Topology style", "Gaya topologi"),
            [("cn", Same("connectivity nodes")), ("nb", Same("node-breaker")), ("bb", Same("bus-branch"))], "cn"),
        new ChoiceParam("profile", new("Show profile", "Tampilkan profil"), [("EQ", Same("EQ")), ("TP", Same("TP")), ("SSH", Same("SSH")), ("SV", Same("SV"))], "EQ"),
        new ChoiceParam("break", new("Break the set", "Rusak set"), Breaks.Select(b => (b.Key, b.Text)).ToList(), "ref"),
    ];

    private static XElement? FirstOf(XDocument d, string cls) => d.Root!.Elements(Cim + cls).FirstOrDefault();

    /// <summary>Applies one deliberate defect to a copy of the set (documents in order EQ, TP, SSH, SV).</summary>
    private static List<XDocument> Break(CgmesDocumentSet set, string key)
    {
        var d = set.All.Select(x => new XDocument(x)).ToList();
        switch (key)
        {
            case "ref":
                FirstOf(d[0], "Terminal")?.Element(Cim + "Terminal.ConductingEquipment")?.SetAttributeValue(Rdf + "resource", "#_00000000-dead-beef-0000-000000000000");
                break;
            case "mand":
                FirstOf(d[0], "ACLineSegment")?.Element(Cim + "ACLineSegment.r")?.Remove();
                break;
            case "param":
                FirstOf(d[0], "ACLineSegment")?.Element(Cim + "ACLineSegment.x")?.SetValue("-0.5");
                break;
            case "kcl":
                if (d.Count > 3 && FirstOf(d[3], "SvPowerFlow")?.Element(Cim + "SvPowerFlow.p") is { } p)
                    p.SetValue((double.Parse(p.Value, CultureInfo.InvariantCulture) + 25).ToString("R", CultureInfo.InvariantCulture));
                break;
            case "header":
                d[1].Root!.Element(Md + "FullModel")?.Element(Md + "Model.scenarioTime")?.Remove();
                break;
            case "dup":
                var lines = d[0].Root!.Elements(Cim + "ACLineSegment").Take(2).ToList();
                if (lines.Count == 2) lines[1].SetAttributeValue(Rdf + "ID", lines[0].Attribute(Rdf + "ID")!.Value);
                break;
        }
        // serialize and reparse, as a consumer would read the files
        return d.Select(x => XDocument.Parse(CgmesDocumentSet.ToXml(x))).ToList();
    }

    private static string Snippet(string xml, int lines)
    {
        var all = xml.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', all.Take(lines)) + (all.Length > lines ? $"\n<!-- … {all.Length - lines} more lines -->" : "");
    }

    private static List<Cell[]> IssueRows(CgmesValidationReport r, string lang, int max = 12)
    {
        var rows = r.Issues.Take(max).Select(i => new Cell[]
        {
            i.RuleId, Cell.Chip(i.Severity.ToString().ToUpperInvariant(), i.Severity switch { CgmesSeverity.Error => "alarm", CgmesSeverity.Warning => "warn", _ => "na" }),
            i.Profile == CgmesProfileKind.Unknown ? "—" : i.Profile.ToString(), i.Message,
        }).ToList();
        if (r.Issues.Count > max) rows.Add(["…", "", "", Pick(lang, $"{r.Issues.Count - max} more findings", $"{r.Issues.Count - max} temuan lain")]);
        if (rows.Count == 0) rows.Add(["—", Cell.Chip("OK", "ok"), "", Pick(lang, "No findings: all 22 rules pass.", "Tidak ada temuan: ke-22 aturan lolos.")]);
        return rows;
    }

    public InteractiveResult Run(ParamValues v, string lang)
    {
        string caseKey = v.Get("case", "case14");
        var style = v.Get("style", "cn") switch { "nb" => CgmesTopologyStyle.NodeBreaker, "bb" => CgmesTopologyStyle.BusBranch, _ => CgmesTopologyStyle.ConnectivityNodes };
        var net = GalleryCases.Load(caseKey);
        var pf = PowerFlowSolver.Solve(net);
        var set = Cgmes3Exporter.Export(net, pf, new Cgmes3ExportOptions { Topology = style });
        byte[] zip = set.ToZip();

        // ---- profiles ---------------------------------------------------------------------------------------------------------
        var profRows = new List<Cell[]>();
        string selected = v.Get("profile", "EQ");
        string snippet = "";
        string selName = "";
        foreach (var (file, doc) in set.Files())
        {
            string xml = CgmesDocumentSet.ToXml(doc);
            var header = CgmesModelHeader.Read(doc);
            var objects = doc.Root!.Elements().Where(e => e.Name.Namespace != Md).ToList();
            var classes = objects.GroupBy(e => e.Name.LocalName).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ×{g.Count()}");
            string kind = header?.Profile switch
            {
                CgmesProfileKind.Equipment => "EQ", CgmesProfileKind.Topology => "TP", CgmesProfileKind.SteadyStateHypothesis => "SSH", CgmesProfileKind.StateVariables => "SV", _ => "?",
            };
            profRows.Add([kind, file, new(Encoding.UTF8.GetByteCount(xml).ToString("N0", CultureInfo.InvariantCulture), null, true),
                new(objects.Count.ToString(CultureInfo.InvariantCulture), null, true), string.Join(", ", classes)]);
            if (kind == selected) { snippet = Snippet(xml, 34); selName = file; }
        }

        // ---- validation -------------------------------------------------------------------------------------------------------
        var clean = CgmesValidator.Validate(set, new CgmesValidationOptions { KclToleranceMw = 1e-4 });
        string bk = v.Get("break", "ref");
        var broken = CgmesValidator.Validate(Break(set, bk), new CgmesValidationOptions { KclToleranceMw = 1e-4 });
        var brk = Breaks.First(b => b.Key == bk);
        var hits = broken.Issues.GroupBy(i => i.RuleId).ToDictionary(g => g.Key, g => g.Count());
        var ruleRows = CgmesValidator.Rules.Select(r => new Cell[]
        {
            r.Id, r.Severity == CgmesSeverity.Error ? Pick(lang, "error", "galat") : Pick(lang, "warning", "peringatan"), r.Description,
            hits.TryGetValue(r.Id, out int n) ? Cell.Chip(n.ToString(CultureInfo.InvariantCulture), r.Severity == CgmesSeverity.Error ? "alarm" : "warn") : Cell.Chip("0", "ok"),
        }).ToList();

        // ---- round trip -------------------------------------------------------------------------------------------------------
        var back = CgmesImporter.Import(CgmesDocumentSet.ReadZip(new MemoryStream(zip)), new CimImportOptions { BaseMva = net.BaseMva });
        var pfBack = PowerFlowSolver.Solve(back.Network);
        double dLoss = Math.Abs(pfBack.LossesMw - pf.LossesMw);

        var kpis = new List<Kpi>
        {
            new(new("CGMES set", "Set CGMES"), set.All.Length.ToString(CultureInfo.InvariantCulture), Pick(lang, "profiles", "profil"),
                Same($"zip {zip.Length / 1024.0:0.0} kB · {back.Version}"), "ok"),
            new(new("Clean set", "Set bersih"), clean.IsValid ? Pick(lang, "VALID", "VALID") : Pick(lang, "INVALID", "TIDAK VALID"), null,
                Same($"{clean.Errors.Count()} {Pick(lang, "errors", "galat")} · {clean.Warnings.Count()} {Pick(lang, "warnings", "peringatan")}"), clean.IsValid ? "ok" : "alarm"),
            new(new("Broken set", "Set rusak"), broken.Errors.Count().ToString(CultureInfo.InvariantCulture), Pick(lang, "errors", "galat"),
                Same(string.Join(" · ", hits.Keys.Take(3))), broken.IsValid ? "warn" : "alarm"),
            new(new("Round trip", "Bolak-balik"), dLoss < 1e-6 ? Pick(lang, "IDENTICAL", "IDENTIK") : Pick(lang, "DIFFERS", "BERBEDA"), null,
                Same($"Δlosses {dLoss:0.0E+0} MW"), dLoss < 1e-6 ? "ok" : "alarm"),
        };

        return new InteractiveResult(kpis,
        [
            new TableBlock(new("Exported profiles", "Profil yang diekspor"), [Same(""), new("File", "Berkas"), Same("Bytes"), new("Objects", "Objek"), new("Most frequent classes", "Kelas terbanyak")], profRows,
                Same($"Cgmes3Exporter.Export(net, pf) · {style}"), Wide: true),
            new ListingBlock(new($"{selected} profile", $"Profil {selected}"), snippet, "xml", Same(selName)),
            new TableBlock(new("Validator report, clean set", "Laporan validator, set bersih"), [new("Rule", "Aturan"), Same(""), new("Profile", "Profil"), new("Message", "Pesan")],
                IssueRows(clean, lang), new($"CgmesValidator.Validate · {clean.Version}", $"CgmesValidator.Validate · {clean.Version}")),
            new TableBlock(new($"Validator report, {brk.Text.En}", $"Laporan validator, {brk.Text.Id}"), [new("Rule", "Aturan"), Same(""), new("Profile", "Profil"), new("Message", "Pesan")],
                IssueRows(broken, lang), brk.Explain, Wide: true),
            new TableBlock(new("Rule catalogue (findings in the broken set)", "Katalog aturan (temuan pada set rusak)"),
                [Same("Id"), new("Severity", "Tingkat"), new("Checks", "Memeriksa"), new("Hits", "Temuan")], ruleRows, Same("CgmesValidator.Rules"), Wide: true),
            new NoteBlock(new("CGMES 3.0 uses the CIM100 namespace and one md:FullModel header per profile; TP and SSH depend on EQ, SV on TP and SSH. mRIDs are name-based UUID v5, so the export is byte-for-byte reproducible. The validator works on any CGMES 2.4.15 or 3.0 set (also from other tools) and reports rule id, severity, profile and the offending object.",
                "CGMES 3.0 memakai namespace CIM100 dan satu header md:FullModel per profil; TP dan SSH bergantung pada EQ, SV pada TP dan SSH. mRID berupa UUID v5 berbasis nama, sehingga ekspor dapat direproduksi byte demi byte. Validator bekerja pada set CGMES 2.4.15 atau 3.0 apa pun (juga dari perangkat lain) dan melaporkan id aturan, tingkat, profil, dan objek yang bermasalah.")),
        ]);
    }
}
