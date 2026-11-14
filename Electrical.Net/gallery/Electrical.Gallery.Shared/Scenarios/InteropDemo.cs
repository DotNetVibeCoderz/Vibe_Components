using System.Globalization;
using Electrical.CIM;
using Electrical.Core.Network;
using Electrical.Interop;
using Electrical.PowerFlow;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// Model exchange: reads the IEEE 14-bus system from PSS/E RAW v33/v35, IEEE CDF, MATPOWER and CIM, detects each format, lists the
/// conversion warnings, and proves with a power flow that every route reaches the same operating point; then writes a case back to RAW.
/// </summary>
public sealed class InteropDemo : IInteractiveScenario
{
    public string Id => "interop";

    private static readonly (string Key, L Text)[] Sources =
    [
        ("raw33", Same("PSS/E RAW v33")), ("raw35", Same("PSS/E RAW v35")), ("cdf", Same("IEEE CDF")), ("matpower", Same("MATPOWER")),
    ];

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new ChoiceParam("source", new("Input file", "Berkas masukan"), Sources, "raw35"),
        new ChoiceParam("case", new("Export case", "Kasus ekspor"),
            [("case9", Same("WSCC 9")), ("case14", Same("IEEE 14")), ("case33bw", Same("Baran–Wu 33"))], "case9"),
        new ChoiceParam("ver", new("RAW revision", "Revisi RAW"), [("33", Same("v33")), ("35", Same("v35"))], "35"),
    ];

    private static string Text(string key) => key switch
    {
        "raw33" => InteropSamples.Ieee14RawV33,
        "raw35" => InteropSamples.Ieee14RawV35,
        "cdf" => InteropSamples.Ieee14Cdf,
        _ => Matpower.Write(TestCases.Case14()),
    };

    private static string Head(string text, int lines)
    {
        var all = text.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', all.Take(lines)) + (all.Length > lines ? $"\n…  ({all.Length - lines} more lines)" : "");
    }

    /// <summary>Largest |ΔV| [pu] and |Δθ| [deg] over the buses two solutions share (matched by id), and the number of shared buses.</summary>
    private static (double Dv, double Da, int Matched) Diff(PowerFlowResult a, PowerFlowResult b)
    {
        double dv = 0, da = 0;
        int n = 0;
        foreach (var x in a.Buses)
        {
            var y = b.Buses.FirstOrDefault(z => z.BusId == x.BusId);
            if (y is null) continue;
            n++;
            dv = Math.Max(dv, Math.Abs(x.Vm - y.Vm));
            da = Math.Max(da, Math.Abs(x.VaDeg - y.VaDeg));
        }
        return n == 0 ? (double.PositiveInfinity, double.PositiveInfinity, 0) : (dv, da, n);
    }

    private static string Sci(double v) => double.IsFinite(v) ? v.ToString("0.0E+0", CultureInfo.InvariantCulture) : "—";

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var reference = TestCases.Case14();
        var pfRef = PowerFlowSolver.Solve(reference);

        // ---- every route into the model --------------------------------------------------------------------------------------
        var routes = new List<(string Label, string Detected, PowerNetwork Net, int Warnings)>();
        foreach (var (key, label) in Sources)
        {
            var r = NetworkFormats.Parse(Text(key));
            routes.Add((label[lang], r.Format.ToString(), r.Network, r.Warnings.Count));
        }
        var cim = CimImporter.Import(CimExporter.ExportString(reference), new CimImportOptions { BaseMva = reference.BaseMva });
        routes.Add(("CIM RDF/XML", "Electrical.CIM", cim, 0));
        foreach (int ver in new[] { 33, 35 })
        {
            var w = new List<string>();
            var r = NetworkFormats.Parse(PsseRaw.Write(reference, ver, w));
            routes.Add(($"case14 → RAW v{ver} → PowerNetwork", r.Format.ToString(), r.Network, r.Warnings.Count + w.Count));
        }

        var rows = new List<Cell[]>();
        int consistent = 0;
        double worst = 0;
        foreach (var (label, detected, net, warnings) in routes)
        {
            var pf = PowerFlowSolver.Solve(net);
            var (dv, da, matched) = Diff(pfRef, pf);
            bool ok = pf.Converged && dv < 1e-4 && Math.Abs(pf.LossesMw - pfRef.LossesMw) < 1e-3;
            if (ok) consistent++;
            if (double.IsFinite(dv)) worst = Math.Max(worst, dv);
            rows.Add(
            [
                label, detected,
                new(matched == reference.BusCount ? net.BusCount.ToString(CultureInfo.InvariantCulture) : $"{net.BusCount} ({matched}*)", null, true),
                new(net.Branches.Count.ToString(CultureInfo.InvariantCulture), null, true),
                new(warnings.ToString(CultureInfo.InvariantCulture), null, true),
                Cell.Num(pf.LossesMw, "0.000"),
                new(Sci(dv), null, true), new(Sci(da), null, true),
                Cell.Chip(ok ? Pick(lang, "same", "sama") : Pick(lang, "differs", "berbeda"), ok ? "ok" : "alarm"),
            ]);
        }

        // ---- selected input: detection + warnings ---------------------------------------------------------------------------
        string srcKey = v.Get("source", "raw35");
        string text = Text(srcKey);
        var detectedFmt = NetworkFormats.Detect(text);
        var imported = NetworkFormats.Parse(text);
        var warnRows = imported.Warnings.Select((w, i) => new Cell[] { new((i + 1).ToString(CultureInfo.InvariantCulture), null, true), w }).ToList();
        if (warnRows.Count == 0) warnRows.Add(["—", Pick(lang, "No conversion warnings: every record maps directly onto the bus-branch model.", "Tidak ada peringatan konversi: setiap rekaman langsung dipetakan ke model bus-cabang.")]);

        // ---- writer ---------------------------------------------------------------------------------------------------------
        string caseKey = v.Get("case", "case9");
        int version = v.Get("ver", "35") == "33" ? 33 : 35;
        var exportNet = GalleryCases.Load(caseKey);
        var writeWarnings = new List<string>();
        string raw = PsseRaw.Write(exportNet, version, writeWarnings);
        var back = PsseRaw.Read(raw);
        var pfA = PowerFlowSolver.Solve(exportNet);
        var pfB = PowerFlowSolver.Solve(back.Network);
        var (rtDv, _, _) = Diff(pfA, pfB);
        bool rtOk = pfB.Converged && rtDv < 1e-6;
        foreach (var w in writeWarnings.Concat(back.Warnings))
            warnRows.Add([Pick(lang, "write", "tulis"), w]);

        var kpis = new List<Kpi>
        {
            new(new("Detected format", "Format terdeteksi"), detectedFmt.ToString(), null, new("from the content, not the file name", "dari isi, bukan nama berkas"), detectedFmt == NetworkFormat.Unknown ? "alarm" : "ok"),
            new(new("Imported", "Diimpor"), imported.Network.BusCount.ToString(CultureInfo.InvariantCulture), Pick(lang, "buses", "bus"),
                Same($"{imported.Network.Branches.Count} {Pick(lang, "branches", "cabang")} · {imported.Network.Generators.Count} gen")),
            new(new("Warnings", "Peringatan"), imported.Warnings.Count.ToString(CultureInfo.InvariantCulture), null, new("approximations made while importing", "pendekatan saat impor"), imported.Warnings.Count > 0 ? "warn" : "ok"),
            new(new("Consistent routes", "Jalur konsisten"), $"{consistent}/{routes.Count}", null, Same($"max |ΔV| {Sci(worst)} pu"), consistent == routes.Count ? "ok" : "alarm"),
            new(new("RAW round trip", "Bolak-balik RAW"), rtOk ? "IDENTICAL" : "DIFFERS", null, Same($"{caseKey} v{version} · |ΔV| {Sci(rtDv)} pu"), rtOk ? "ok" : "alarm"),
        };

        return new InteractiveResult(kpis,
        [
            new TableBlock(new("Cross-format power-flow consistency (IEEE 14-bus)", "Konsistensi aliran daya lintas format (IEEE 14 bus)"),
                [new("Route", "Jalur"), new("Read as", "Dibaca sebagai"), Same("Bus"), new("Branches", "Cabang"), new("Warn.", "Peringatan"),
                 new("Losses MW", "Rugi MW"), Same("max |ΔV| pu"), Same("max |Δθ| °"), Same("")], rows,
                new($"reference: TestCases.Case14(), {F(pfRef.LossesMw, "0.000")} MW losses (MATPOWER 13.393)",
                    $"acuan: TestCases.Case14(), rugi {F(pfRef.LossesMw, "0.000")} MW (MATPOWER 13,393)"), Wide: true),
            new ListingBlock(new("Input file", "Berkas masukan"), Head(text, 26), "text",
                new($"NetworkFormats.Detect → {detectedFmt}", $"NetworkFormats.Detect → {detectedFmt}")),
            new ListingBlock(new($"PsseRaw.Write(…, {version})", $"PsseRaw.Write(…, {version})"), Head(raw, 26), "text",
                new($"{caseKey}, {raw.Split('\n').Length} lines", $"{caseKey}, {raw.Split('\n').Length} baris")),
            new TableBlock(new("Conversion warnings", "Peringatan konversi"), [Same("#"), new("Message", "Pesan")], warnRows,
                new("ImportResult.Warnings", "ImportResult.Warnings"), Wide: true),
            new NoteBlock(new("Detection looks at the content: \"mpc.bus\" means MATPOWER, \"BUS DATA FOLLOWS\" means IEEE CDF, and a first record \"IC, SBASE, REV\" with REV 20–40 means PSS/E RAW. Three-winding transformers are converted to a star equivalent and switched shunts are frozen at their initial value; each approximation is reported as a warning (the RAW v35 sample models 4-7-8-9 as a real three-winding transformer, so bus 7 is replaced by an internal star bus: * marks the buses compared). CIM goes through Electrical.CIM (CimImporter).",
                "Deteksi memeriksa isi: \"mpc.bus\" berarti MATPOWER, \"BUS DATA FOLLOWS\" berarti IEEE CDF, dan rekaman pertama \"IC, SBASE, REV\" dengan REV 20–40 berarti PSS/E RAW. Trafo tiga belitan diubah menjadi ekuivalen bintang dan shunt terkendali dibekukan pada nilai awalnya; setiap pendekatan dilaporkan sebagai peringatan (sampel RAW v35 memodelkan 4-7-8-9 sebagai trafo tiga belitan sungguhan, sehingga bus 7 diganti bus bintang internal: * menandai bus yang dibandingkan). CIM melalui Electrical.CIM (CimImporter).")),
        ]);
    }
}
