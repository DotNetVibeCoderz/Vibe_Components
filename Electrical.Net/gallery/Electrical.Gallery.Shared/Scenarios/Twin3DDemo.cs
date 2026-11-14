using System.Globalization;
using Electrical.Core.Network;
using Electrical.DigitalTwin.ThreeD;
using Electrical.PowerFlow;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// 3D digital twin (Electrical.DigitalTwin.ThreeD): a procedural substation/feeder scene coloured by a power flow at the chosen load
/// scale. The web app shows an off-screen ThreeNet render (PNG) when a GPU adapter is available and always offers the scene as glTF
/// binary (GLB); the desktop app hosts the live <c>GridTwin3DView</c> instead of the PNG.
/// </summary>
public sealed class Twin3DDemo : IInteractiveScenario
{
    public string Id => "twin3d";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (PowerNetwork Net, GridScene3D Scene)> Scenes = [];
    private static bool? _gpu;
    private static string? _gpuError;

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new ChoiceParam("case", new("Network", "Jaringan"), [("case14", Same("IEEE 14")), ("case9", Same("WSCC 9")), ("case33bw", Same("Baran–Wu 33"))], "case14"),
        new RangeParam("scale", new("Load scale", "Skala beban"), 0.6, 1.6, 0.05, 1.0, "0.00", "×"),
    ];

    /// <summary>Network with ratings (140 % of the base-case flow for unrated branches, so colours respond to the load scale).</summary>
    public static PowerNetwork Network(string caseKey)
    {
        lock (Gate) return Entry(caseKey).Net;
    }

    private static (PowerNetwork Net, GridScene3D Scene) Entry(string caseKey)
    {
        if (Scenes.TryGetValue(caseKey, out var e)) return e;
        var net = GalleryCases.Load(caseKey);
        GalleryCases.AssignRatings(net, PowerFlowSolver.Solve(net));
        e = (net, GridSceneBuilder.Build(net, new GridSceneOptions { BranchLabels = false }));
        Scenes[caseKey] = e;
        return e;
    }

    /// <summary>A fresh scene for a view that owns it (the desktop control); colours applied for <paramref name="scale"/>.</summary>
    public static GridScene3D CreateScene(string caseKey, double scale)
    {
        var net = Network(caseKey);
        var s = GridSceneBuilder.Build(net, new GridSceneOptions { BranchLabels = false });
        s.ApplyPowerFlow(Solve(net, scale));
        return s;
    }

    public static PowerFlowResult Solve(PowerNetwork net, double scale)
    {
        var pf = PowerFlowSolver.Solve(GalleryCases.Scaled(net, scale));
        if (!pf.Converged) throw new InvalidOperationException($"The power flow does not converge at {scale:0.00}× load.");
        return pf;
    }

    public InteractiveResult Run(ParamValues v, string lang) => Run(v, lang, renderPng: true);

    /// <summary>Computes the study; <paramref name="renderPng"/> = false skips the off-screen render (the desktop shows the live view).</summary>
    public InteractiveResult Run(ParamValues v, string lang, bool renderPng)
    {
        string caseKey = v.Get("case", "case14");
        double scale = v.D("scale");
        byte[] glb;
        byte[]? png = null;
        PowerFlowResult pf;
        GridScene3D scene;
        lock (Gate)
        {
            var e = Entry(caseKey);
            scene = e.Scene;
            pf = Solve(e.Net, scale);
            scene.ApplyPowerFlow(pf);
            glb = GlbExporter.Export(scene);
            if (renderPng && _gpu != false)
            {
                try
                {
                    using var three = ThreeNetGridScene.Create(scene);
                    three.FrameCamera(elevationDeg: 42, azimuthDeg: 28, zoom: 0.5f);
                    png = three.RenderPng(1280, 720);
                    _gpu = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _gpu = false;
                    _gpuError = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        var net = pf.Network;
        var heavy = pf.Branches.Select(b => (b, Load: net.Branches[b.Index].RateA > 0 ? 100 * Math.Max(b.SFrom, b.STo) / net.Branches[b.Index].RateA : double.NaN))
            .OrderByDescending(x => x.Load).Take(8).ToList();
        var rows = heavy.Select(x => new Cell[]
        {
            $"{x.b.From}–{x.b.To}", net.Branches[x.b.Index].IsTransformer ? Pick(lang, "transformer", "trafo") : Pick(lang, "line", "saluran"),
            Cell.Num(Math.Max(x.b.SFrom, x.b.STo), "0.0"), Cell.Num(net.Branches[x.b.Index].RateA, "0"),
            Cell.Chip(F(x.Load, "0") + " %", x.Load > 100 ? "alarm" : x.Load > 80 ? "warn" : "ok"),
        }).ToList();
        double maxLoad = heavy.Count == 0 ? 0 : heavy[0].Load;
        var min = pf.MinVoltageBus;

        var kpis = new List<Kpi>
        {
            new(new("Scene", "Adegan"), scene.Objects.Count.ToString(CultureInfo.InvariantCulture), Pick(lang, "objects", "objek"),
                Same($"{scene.TriangleCount:N0} {Pick(lang, "triangles", "segitiga")} · {scene.Count(GridElementKind.Tower)} {Pick(lang, "towers", "menara")}")),
            new(new("Max loading", "Pembebanan maks"), F(maxLoad, "0"), "%", Same(heavy.Count > 0 ? $"{heavy[0].b.From}–{heavy[0].b.To}" : ""), maxLoad > 100 ? "alarm" : maxLoad > 80 ? "warn" : "ok"),
            new(new("Min voltage", "Tegangan min"), F(min.Vm, "0.000"), "pu", Same($"bus {min.BusId}"), min.Vm < 0.95 ? "alarm" : "ok"),
            new(new("glTF binary", "glTF biner"), F(glb.Length / 1024.0, "0"), "kB", Same("GlbExporter.Export")),
        };
        var blocks = new List<Block>();
        if (png is not null)
            blocks.Add(new ImageBlock(new("Off-screen ThreeNet render", "Render ThreeNet di luar layar"), png,
                new($"3D view of {net.Name} at {F(scale, "0.00")}× load: busbars coloured by voltage, conductors by loading", $"Tampilan 3D {net.Name} pada beban {F(scale, "0.00")}×: busbar diwarnai menurut tegangan, konduktor menurut pembebanan"),
                Same($"ThreeNetGridScene.RenderPng · 1280×720 · {png.Length / 1024} kB")));
        else if (renderPng)
            blocks.Add(new NoteBlock(new($"No GPU adapter is available to this process, so the off-screen render is skipped ({_gpuError}). Download the GLB below and open it in any glTF viewer (Windows 3D Viewer, Blender, three.js editor), or run the desktop gallery for the live view.",
                $"Tidak ada adaptor GPU untuk proses ini, sehingga render di luar layar dilewati ({_gpuError}). Unduh GLB di bawah dan buka di penampil glTF apa pun (Windows 3D Viewer, Blender, editor three.js), atau jalankan galeri desktop untuk tampilan langsung.")));
        blocks.Add(new TableBlock(new("Most loaded branches", "Cabang terbebani terberat"), [new("Branch", "Cabang"), new("Kind", "Jenis"), Same("MVA"), new("Rating", "Rating"), new("Loading", "Pembebanan")], rows,
            new("ratings: 140 % of the base-case flow where the case has none", "rating: 140 % aliran kasus dasar bila kasus tidak memilikinya")));
        blocks.Add(new DownloadBlock(new("Scene as glTF 2.0", "Adegan sebagai glTF 2.0"), $"{net.Name}-{scale.ToString("0.00", CultureInfo.InvariantCulture)}.glb", "model/gltf-binary", glb,
            new($"{scene.Objects.Count(o => o.Mesh.VertexCount > 0)} meshes with PBR materials in the current colours. Opens in Blender, Windows 3D Viewer or any glTF viewer.",
                $"{scene.Objects.Count(o => o.Mesh.VertexCount > 0)} mesh dengan material PBR dalam warna saat ini. Dapat dibuka di Blender, Windows 3D Viewer, atau penampil glTF apa pun."),
            Same("GLB")));
        blocks.Add(new NoteBlock(new("GridSceneBuilder lays the single-line diagram out in 3D: a busbar with gantries per bus, breaker and disconnectors at each branch end, transformers, towers every span with sagging catenary conductors (cables on the feeder), generators and loads. ApplyPowerFlow colours busbars by voltage (blue low, red high) and conductors by loading (green → amber → red); open branches turn grey. Raise the load scale to watch the heavy corridors change colour.",
            "GridSceneBuilder menata diagram segaris dalam 3D: busbar dengan gantry per bus, pemutus dan pemisah di setiap ujung cabang, trafo, menara tiap gawang dengan konduktor katenari yang melendut (kabel pada penyulang), pembangkit, dan beban. ApplyPowerFlow mewarnai busbar menurut tegangan (biru rendah, merah tinggi) dan konduktor menurut pembebanan (hijau → kuning → merah); cabang terbuka menjadi abu-abu. Naikkan skala beban untuk melihat koridor berat berubah warna.")));
        return new InteractiveResult(kpis, blocks);
    }
}
