using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Electrical.Core.Network;
using Electrical.Gallery.Shared;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.Visualization;
using static Electrical.Gallery.Desktop.Ui;

namespace Electrical.Gallery.Desktop;

/// <summary>Desktop renderings of every gallery demo (scenario logic shared with the web app via Electrical.Gallery.Shared).</summary>
internal static class Views
{
    /// <summary>Maps a catalog id to its view. Add a case here when you add a demo.</summary>
    public static Control Build(string id) => id switch
    {
        "powerflow" => PowerFlow(),
        "contingency" => Contingency(),
        "plf" => Plf(),
        "scada" => Scada(),
        "iec61850" => Iec61850(),
        "cim" => Cim(),
        "gis" => Gis(),
        "agent" => Agent(),
        _ when InteractiveScenarios.Find(id) is { } scenario => Interactive(scenario),
        _ => Text(T("This demo has no desktop view yet.", "Demo ini belum memiliki tampilan desktop."), 14, Pal.Ink3),
    };

    /// <summary>Content host that re-renders when <see cref="Refresh"/> is called (control-driven views).</summary>
    private sealed class Live : ContentControl
    {
        private readonly Func<Control> _render;
        public Live(Func<Control> render) { _render = render; Content = render(); }
        public void Refresh() => Content = _render();
    }

    private static IEnumerable<(string, string, bool)> Cases(IEnumerable<string> keys) =>
        keys.Select(k => (k, GalleryCases.All.First(c => c.Key == k).Label[Lang], true));

    // ------------------------------------------------------------------ home

    public static Control Home(Action<string> navigate)
    {
        var net = TestCases.Case14();
        var pf = PowerFlowSolver.Solve(net);
        var sld = SldRenderer.Render(net, pf, new SldOptions { ShowLegend = false, LayerGap = 120, BusGap = 48 });
        var min = pf.MinVoltageBus;
        Kpi[] kpis =
        [
            new(new("Generation", "Pembangkitan"), F(pf.TotalGenerationMw, "0.0"), "MW", null, "ok"),
            new(new("Losses", "Rugi-rugi"), F(pf.LossesMw, "0.00"), "MW"),
            new(new("Min voltage", "Tegangan min"), F(min.Vm, "0.000"), "pu", new($"bus {min.BusId}", $"bus {min.BusId}")),
            new(new("Max voltage", "Tegangan maks"), F(pf.MaxVoltageBus.Vm, "0.000"), "pu", new($"bus {pf.MaxVoltageBus.BusId}", $"bus {pf.MaxVoltageBus.BusId}"), "warn"),
        ];

        var h1 = Display(T("Power-system engineering", "Rekayasa sistem tenaga"), 50);
        var h2 = Text(T("in C#, solved in this window.", "dalam C#, dihitung di jendela ini."), 30, Pal.Ink3, FontWeight.SemiBold, "Display");
        var lede = Text(T("Power flow, contingency screening, Monte-Carlo studies, SCADA telemetry, IEC 61850 messages, CIM exchange and grid maps — every figure is computed by the library in-process, next to the C# that produced it.",
                          "Aliran daya, penapisan kontingensi, studi Monte-Carlo, telemetri SCADA, pesan IEC 61850, pertukaran CIM, dan peta jaringan — setiap angka dihitung pustaka di dalam proses, berdampingan dengan kode C# yang menghasilkannya."), 15.5, Pal.Ink2);
        var go = new Button { Content = T("Run a power flow  →", "Jalankan aliran daya  →"), Background = Pal.Ink, Foreground = Pal.Bg, Padding = new Thickness(16, 9), CornerRadius = new CornerRadius(6), FontWeight = FontWeight.SemiBold };
        go.Click += (_, _) => navigate("powerflow");
        var copy = Stack(14, Eyebrow($"{T("Electrical.Net gallery", "Galeri Electrical.Net")} · {GalleryCatalog.Demos.Length} {T("live demos", "demo langsung")}", Pal.Cyan), h1, h2, lede, go,
            Code("var net = TestCases.Case14();\nvar pf  = PowerFlowSolver.Solve(net);   // Newton–Raphson\nvar svg = SldRenderer.Render(net, pf);  // diagram →", "Program.cs"));

        var mimicHead = new DockPanel { Margin = new Thickness(14, 10) };
        var right = Mono($"{T("CONVERGED", "KONVERGEN")} · {pf.Iterations} it · {F(pf.Elapsed.TotalMilliseconds, "0.0")} ms", 11, Pal.Ink3);
        DockPanel.SetDock(right, Dock.Right);
        mimicHead.Children.Add(right);
        mimicHead.Children.Add(Row(8, Lamp(true), Mono("IEEE 14-BUS · NEWTON–RAPHSON", 11, Pal.Ink3)));
        var mimic = new Border
        {
            Background = Pal.Panel, BorderBrush = Pal.Rule2, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Child = Stack(0,
                new Border { BorderBrush = Pal.Rule, BorderThickness = new Thickness(0, 0, 0, 1), Child = mimicHead },
                new Border { Background = Pal.Panel2, Padding = new Thickness(12, 8), Child = SvgView(sld, 430) },
                Annunciator(kpis, 4)),
        };
        var hero = Columns("1*,1.15*", copy, mimic);

        var groups = new StackPanel { Spacing = 26 };
        foreach (var c in GalleryCatalog.Categories)
        {
            var cards = new UniformGrid { Columns = 3 };
            foreach (var d in GalleryCatalog.Demos.Where(x => x.Category == c.Key))
            {
                var card = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = Pal.Panel, BorderBrush = Pal.Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 16),
                    Margin = new Thickness(0, 0, 14, 14),
                    Content = Stack(6, Mono("/demo/" + d.Id, 11.5, Pal.Ink3), Text(d.Title[Lang], 20, Pal.Ink, FontWeight.Bold, "Display"), Text(d.Summary[Lang], 13.5, Pal.Ink2),
                        Mono(string.Join(" · ", d.Modules), 11.5, Pal.Cyan)),
                };
                var id = d.Id;
                card.Click += (_, _) => navigate(id);
                cards.Children.Add(card);
            }
            groups.Children.Add(Stack(12, SectionHead(c.Name[Lang], c.Blurb[Lang]), cards));
        }

        var bays = new UniformGrid { Columns = 6 };
        foreach (var m in GalleryCatalog.Modules)
        {
            var demos = GalleryCatalog.DemosFor(m.Name).ToList();
            bays.Children.Add(new Border
            {
                Background = demos.Count > 0 ? Pal.Panel : Pal.Panel2, Margin = new Thickness(0, 0, 1, 1), Padding = new Thickness(14, 12), MinHeight = 120,
                Child = Stack(6, Row(9, Lamp(demos.Count > 0), Display(m.Name, 16, demos.Count > 0 ? Pal.Ink : Pal.Ink2)), Text(m.Summary[Lang], 12, Pal.Ink3),
                    Mono(demos.Count == 0 ? T("demo coming", "demo menyusul") : string.Join(" · ", demos.Select(x => x.Id)), 11.5, demos.Count == 0 ? Pal.Ink3 : Pal.Cyan)),
            });
        }
        var panel = new Border { Background = Pal.Rule2, BorderBrush = Pal.Rule2, BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = bays };
        var modules = Stack(12, SectionHead(T("Module panel", "Panel modul"),
            T($"All {GalleryCatalog.Modules.Length} packages. A lit lamp means the module has a live demo.", $"Semua {GalleryCatalog.Modules.Length} paket. Lampu menyala berarti modul memiliki demo langsung.")), panel);

        return Stack(40, hero, groups, modules, Text($"Electrical.Net — {GalleryCatalog.Tagline[Lang]} · {GalleryCatalog.Credit[Lang]}", 12.5, Pal.Ink3));
    }

    private static Control SectionHead(string title, string blurb) => new Border
    {
        BorderBrush = Pal.Rule2, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 8),
        Child = Row(16, Display(title, 24), new TextBlock { Text = blurb, Foreground = Pal.Ink3, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) }),
    };

    // ------------------------------------------------------------------ power flow

    private static Control PowerFlow()
    {
        string cs = "case14"; double scale = 1.0; var method = PowerFlowMethod.NewtonRaphson;
        PowerFlowDemoResult r = PowerFlowScenario.Run(cs, scale, method, Lang);
        Live? live = null; ContentControl controls = new();
        void Run() { r = PowerFlowScenario.Run(cs, scale, method, Lang); method = r.Method; controls.Content = Bar(); live!.Refresh(); }
        Control Bar() => Ui.Controls(
            Seg(T("Test system", "Sistem uji"), Cases(GalleryCases.All.Select(c => c.Key)), cs, v => { cs = v; Run(); }),
            Seg(T("Method", "Metode"), PowerFlowScenario.MethodNames.Select(m => (m.Method.ToString(), m.Short, m.Method != PowerFlowMethod.BackwardForwardSweep || r.Radial)), method.ToString(),
                v => { method = Enum.Parse<PowerFlowMethod>(v); Run(); }),
            Slider(T("Load scale", "Skala beban"), 0.6, 1.6, scale, 0.05, v => { scale = v; Run(); }, "0.00", "×"));
        controls.Content = Bar();
        live = new Live(() =>
        {
            var rows = r.Methods.Select(m => new object[]
            {
                m.Label, !m.Applicable ? State("N/A", "na") : m.Converged ? State(T("converged", "konvergen"), "ok") : State(T("diverged", "divergen"), "alarm"),
                m.Applicable ? m.Iterations.ToString(CultureInfo.InvariantCulture) : "—", m.Applicable ? F(m.Milliseconds, "0.00") : "—", F(m.LossesMw, "0.000"),
                m.Applicable ? $"{F(m.MinVm, "0.0000")} @ {m.MinVmBus}" : "—", m.Note is { } n ? T(n) : "",
            });
            return Stack(16,
                Annunciator(r.Kpis),
                Panel(T("Single-line diagram", "Diagram segaris"), SvgView(r.SldSvg, 640), $"{r.Network.Name} · {r.Network.BusCount} {T("buses", "bus")} · {PowerFlowScenario.Short(r.Method)}"),
                Columns("*,*",
                    Panel(T("Voltage profile", "Profil tegangan"), SvgView(r.VoltageSvg), "|V| pu"),
                    Panel(T("Convergence", "Konvergensi"), SvgView(r.ConvergenceSvg), T("mismatch per iteration", "mismatch per iterasi"))),
                Panel(T("Method comparison", "Perbandingan metode"),
                    Table([T("Method", "Metode"), "Status", T("Iterations", "Iterasi"), "ms", T("Losses MW", "Rugi MW"), "Vmin pu", T("Note", "Catatan")], rows, [2, 3, 4, 5]),
                    T("same operating point", "titik operasi yang sama")));
        });
        return Stack(16, controls, live);
    }

    // ------------------------------------------------------------------ contingency

    private static Control Contingency()
    {
        string cs = "case9"; double scale = 1.0;
        Live? live = null;
        live = new Live(() =>
        {
            var r = ContingencyScenario.Run(cs, scale, Lang);
            var rows = r.Ranking.Select((c, i) => new object[]
            {
                (i + 1).ToString(CultureInfo.InvariantCulture), c.Outage,
                c.Islanding ? State(T("islanding", "pulau"), "warn") : !c.Converged ? State(T("diverged", "divergen"), "alarm")
                    : c.Overloads + c.VoltageViolations > 0 ? State(T("insecure", "tidak aman"), "alarm") : State(T("secure", "aman"), "ok"),
                Row(8, Meter(c.MaxLoadingPercent / 1.5, c.MaxLoadingPercent > 100 ? "alarm" : c.MaxLoadingPercent > 80 ? "warn" : "ok"), Mono(F(c.MaxLoadingPercent, "0.0") + " %", 12.5, Pal.Ink)),
                c.MostLoadedBranch ?? "—", c.Islanding ? "—" : $"{F(c.MinVoltage, "0.000")} @ {c.MinVoltageBus}", c.Overloads.ToString(CultureInfo.InvariantCulture),
                c.VoltageViolations.ToString(CultureInfo.InvariantCulture), double.IsInfinity(c.Severity) ? "∞" : F(c.Severity, "0.0"),
            });
            var items = new List<Control> { Annunciator(r.Kpis) };
            if (r.RatingsAssumed) items.Add(Notice(T("This case has no branch ratings; the demo assumes 140 % of each branch's base-case flow.", "Kasus ini tidak memiliki rating cabang; demo mengasumsikan 140 % dari aliran kasus dasar.")));
            items.Add(Columns("*,*",
                Panel(T("Worst post-contingency loading", "Pembebanan terburuk pasca-kontingensi"), SvgView(r.ChartSvg), T("top 12", "12 teratas")),
                Panel(T("After the worst outage", "Setelah gangguan terburuk"), SvgView(r.WorstSldSvg, 380), $"{r.WorstOutage} {T("out", "lepas")}")));
            items.Add(Panel(T("Contingency ranking", "Peringkat kontingensi"),
                Table(["#", T("Outage", "Gangguan"), T("Result", "Hasil"), T("Max loading", "Pembebanan maks"), T("On branch", "Di cabang"), "Vmin pu", T("Overloads", "Beban lebih"), T("V viol.", "Pelanggaran V"), T("Severity", "Keparahan")],
                    rows, [0, 5, 6, 7, 8], 16), $"{r.Ranking.Count} {T("outages", "gangguan")} · {F(r.Milliseconds, "0")} ms"));
            return Stack(16, [.. items]);
        });
        Control controls = null!;
        ContentControl host = new();
        Control Bar() => Ui.Controls(
            Seg(T("Test system", "Sistem uji"), Cases(ContingencyScenario.Cases), cs, v => { cs = v; host.Content = Bar(); live.Refresh(); }),
            Slider(T("Load scale", "Skala beban"), 0.8, 1.4, scale, 0.05, v => { scale = v; live.Refresh(); }, "0.00", "×"));
        host.Content = controls = Bar();
        return Stack(16, host, live);
    }

    // ------------------------------------------------------------------ probabilistic load flow

    private static Control Plf()
    {
        string cs = "case33bw"; int samples = 1000; double std = 0.10;
        Live? live = null;
        live = new Live(() =>
        {
            var r = PlfScenario.Run(cs, samples, std, Lang);
            var rows = r.Result.Buses.OrderByDescending(b => b.ProbabilityUnderVoltage).ThenBy(b => b.Mean).Take(9).Select(b => new object[]
            {
                b.BusId.ToString(CultureInfo.InvariantCulture), F(b.Mean, "0.000"), F(b.P05, "0.000"),
                Row(8, Meter(100 * b.ProbabilityUnderVoltage, b.ProbabilityUnderVoltage > 0.5 ? "alarm" : b.ProbabilityUnderVoltage > 0.05 ? "warn" : "ok"), Mono(F(100 * b.ProbabilityUnderVoltage, "0") + " %", 12.5, Pal.Ink)),
            });
            return Stack(16,
                Annunciator(r.Kpis),
                Columns("*,*",
                    Panel(T("Losses", "Rugi-rugi"), SvgView(r.LossHistogramSvg), T("distribution", "sebaran")),
                    Panel(T("Minimum voltage", "Tegangan minimum"), SvgView(r.VminHistogramSvg), T("lowest bus per sample", "bus terendah per sampel"))),
                Columns("1.7*,*",
                    Panel(T("Voltage band per bus", "Pita tegangan per bus"), SvgView(r.VoltageBandSvg), "P05 · mean · P95"),
                    Panel(T("Under-voltage risk", "Risiko tegangan rendah"), Table(["Bus", T("mean", "rata2"), "P05", T("risk", "risiko")], rows, [0, 1, 2]), "P(V < Vmin)")),
                Text(T($"Gaussian draws: {r.Backend} kernel · each sample is a warm-started Newton–Raphson solve under Parallel.For.",
                       $"Bilangan acak Gauss: kernel {r.Backend} · setiap sampel adalah solusi Newton–Raphson warm-start dengan Parallel.For."), 12.5, Pal.Ink3));
        });
        ContentControl host = new();
        Control Bar() => Ui.Controls(
            Seg(T("Test system", "Sistem uji"), Cases(PlfScenario.Cases), cs, v => { cs = v; host.Content = Bar(); live.Refresh(); }),
            Seg(T("Samples", "Sampel"), [("250", "250", true), ("1000", "1 000", true), ("4000", "4 000", true)], samples.ToString(CultureInfo.InvariantCulture),
                v => { samples = int.Parse(v, CultureInfo.InvariantCulture); host.Content = Bar(); live.Refresh(); }),
            Slider(T("Load σ", "σ beban"), 0.02, 0.25, std, 0.01, v => { std = v; live.Refresh(); }, "0%"));
        host.Content = Bar();
        return Stack(16, host, live);
    }

    // ------------------------------------------------------------------ SCADA

    private static (string Letter, string Tone) AlarmLook(AlarmState s) => s switch
    {
        AlarmState.Unacknowledged => ("B", "alarm"), AlarmState.Acknowledged => ("C", "warn"), AlarmState.ReturnToNormalUnacknowledged => ("D", "info"),
        AlarmState.Shelved => ("E", "na"), _ => ("A", "ok"),
    };

    private static Control Scada()
    {
        double peak = 1.3, dev = 0.5;
        Live? live = null;
        live = new Live(() =>
        {
            var r = ScadaScenario.Run(peak, dev, Lang);
            var alarms = new StackPanel { Spacing = 6 };
            foreach (var a in r.Alarms)
            {
                var (letter, tone) = AlarmLook(a.State);
                IBrush c = tone switch { "alarm" => Pal.Red, "warn" => Pal.Amber, "info" => Pal.Cyan, "ok" => Pal.Green, _ => Pal.LampOff };
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,Auto,Auto,Auto"), ColumnSpacing = 10 };
                Control[] cells =
                [
                    new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(3), Background = c, Child = new TextBlock { Text = letter, FontFamily = Font("Mono"), FontWeight = FontWeight.Bold, FontSize = 12, Foreground = Pal.Hex("#04121C"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                    Mono(a.Tag, 12.5, Pal.Ink), Mono(a.Limit ?? "—", 12, Pal.Ink3), Mono(F(a.Value, Math.Abs(a.Value) < 10 ? "0.000" : "0.0"), 12, Pal.Ink3),
                    Mono(a.Priority.ToString().ToUpperInvariant(), 10.5, (int)a.Priority >= 3 ? Pal.Red : Pal.Ink3),
                ];
                for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); cells[i].VerticalAlignment = VerticalAlignment.Center; g.Children.Add(cells[i]); }
                alarms.Children.Add(new Border
                {
                    Background = tone == "alarm" ? Pal.Glow("alarm") : Pal.Panel2, BorderBrush = c, BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 6), Child = g,
                });
            }
            var journal = new StackPanel();
            foreach (var e in r.Events)
                journal.Children.Add(Mono($"{e.Time.ToString("HH:mm", CultureInfo.InvariantCulture)}  {e.Tag,-9} {AlarmLook(e.From).Letter} → {AlarmLook(e.To).Letter}", 12, e.To == AlarmState.Unacknowledged ? Pal.Red : Pal.Ink2));
            var points = r.Points.Select(p => new object[] { p.Name, p.Table, p.Address.ToString(CultureInfo.InvariantCulture), Mono(p.Registers, 12, Pal.Cyan), F(p.Value, "0.0000"), F(p.TrueValue, "0.0000"), p.Unit });
            var wire = Stack(8,
                Eyebrow(T("Request ADU · master → RTU", "ADU permintaan · master → RTU")), HexBox(r.ModbusRequestHex),
                Eyebrow(T("Response ADU · RTU → master", "ADU respons · RTU → master")), HexBox(r.ModbusResponseHex),
                Text(T("Served in-process by RtuSimulator.Map.Process; ModbusTcpServer sends the same bytes over TCP.", "Dilayani di dalam proses oleh RtuSimulator.Map.Process; ModbusTcpServer mengirim byte yang sama lewat TCP."), 12.5, Pal.Ink3));
            return Stack(16,
                Annunciator(r.Kpis),
                Columns("1.7*,*",
                    Panel(T("Historian: line 1–2 flow", "Historian: aliran saluran 1–2"), SvgView(r.HistorianSvg), $"swinging-door ±{F(dev, "0.0")} MW · {r.ArchivedSamples}/{r.RawSamples}"),
                    Panel(T("Alarm summary", "Ringkasan alarm"), r.Alarms.Count == 0 ? Text(T("No alarms today.", "Tidak ada alarm hari ini."), 13, Pal.Ink3) : alarms, "ISA-18.2")),
                Columns("1.7*,*",
                    Panel(T("Bus 14 voltage trend", "Tren tegangan bus 14"), SvgView(r.VoltageTrendSvg), T("resampled, 15 min", "diresampel, 15 mnt")),
                    Panel(T("Alarm journal", "Jurnal alarm"), new ScrollViewer { Content = journal, MaxHeight = 230 }, $"{r.Events.Count} {T("events", "kejadian")}")),
                Columns("*,*",
                    Panel(T("RTU register map", "Peta register RTU"), Table([T("Point", "Titik"), T("Table", "Tabel"), T("Addr", "Alamat"), "Registers", T("Read", "Terbaca"), T("True", "Sebenarnya"), T("Unit", "Satuan")], points, [2, 4, 5], 14), "Float32, hi-word first"),
                    Panel(T("Modbus TCP exchange", "Pertukaran Modbus TCP"), wire, "FC 04 · bus 14")));
        });
        var bar = Ui.Controls(
            Slider(T("Evening peak", "Puncak malam"), 0.8, 1.7, peak, 0.05, v => { peak = v; live.Refresh(); }, "0.00", "× base"),
            Slider(T("Historian deviation", "Deviasi historian"), 0.1, 4, dev, 0.1, v => { dev = v; live.Refresh(); }, "0.0", " MW"),
            Stack(6, Eyebrow(T("Scenario", "Skenario")), Text($"IEEE 14-bus · 288 × 5 min · {T("operator acknowledges at 12:00", "operator mengakui pukul 12:00")}", 13, Pal.Ink2)));
        return Stack(16, bar, live);
    }

    private static Control HexBox(string hex) => new Border
    {
        Background = Pal.CodeBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9),
        Child = new SelectableTextBlock { Text = hex, FontFamily = Font("Mono"), FontSize = 13, Foreground = Pal.Hex("#22D3EE"), TextWrapping = TextWrapping.Wrap },
    };

    // ------------------------------------------------------------------ IEC 61850

    private static IBrush SegBrush(string? key) => key switch
    {
        "dst" => Pal.Hex(Pal.Dark ? "#3A2F6E" : "#E4DCFB"), "src" => Pal.Hex(Pal.Dark ? "#22386B" : "#DCE6FA"), "vlan" => Pal.Hex(Pal.Dark ? "#4A2244" : "#F5DCF0"),
        "type" => Pal.Hex(Pal.Dark ? "#4A3A12" : "#FBEBCB"), "appid" => Pal.Hex(Pal.Dark ? "#173F2E" : "#D6F0E2"), "pdu" => Pal.Hex(Pal.Dark ? "#0E3A4A" : "#D5EEF3"),
        _ => Brushes.Transparent,
    };

    private static IBrush SegSwatch(string key) => key switch
    {
        "dst" => Pal.Hex("#8B6CF0"), "src" => Pal.Hex("#4F7FE0"), "vlan" => Pal.Hex("#C955B5"), "type" => Pal.Amber, "appid" => Pal.Green, "pdu" => Pal.Cyan, _ => Pal.LampOff,
    };

    private static Control Iec61850()
    {
        string kind = "goose"; bool open = true, vlan = true; int sq = 0; double amps = 400, phi = 25;
        Live? live = null;
        live = new Live(() =>
        {
            var r = Iec61850Scenario.Run(kind, open, sq, vlan, amps, phi, Lang);
            var dump = new SelectableTextBlock { FontFamily = Font("Mono"), FontSize = 13, LineHeight = 23, Inlines = new InlineCollection() };
            foreach (var row in HexDump.Rows(r.Frame))
            {
                dump.Inlines.Add(new Run(row.Offset.ToString("X4", CultureInfo.InvariantCulture) + "   ") { Foreground = Pal.Ink3 });
                for (int i = 0; i < 16; i++)
                {
                    if (i < row.Bytes.Length)
                        dump.Inlines.Add(new Run(row.Bytes[i].ToString("X2", CultureInfo.InvariantCulture)) { Foreground = Pal.Ink, Background = SegBrush(HexDump.KeyAt(r.Segments, row.Offset + i)) });
                    else dump.Inlines.Add(new Run("  "));
                    dump.Inlines.Add(new Run(" "));
                }
                dump.Inlines.Add(new Run("  " + string.Concat(row.Bytes.Select(HexDump.Ascii)) + "\n") { Foreground = Pal.Ink3 });
            }
            var legend = new WrapPanel();
            foreach (var s in r.Segments)
                legend.Children.Add(Row(5, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = SegSwatch(s.Key) }, Text($"{T(s.Label)} ({s.End - s.Start} B)", 12, Pal.Ink2, wrap: false)).WithMargin(0, 6, 14, 0));
            var fields = new StackPanel();
            foreach (var f in r.Fields)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("40,Auto,*"), ColumnSpacing = 10, Margin = new Thickness(0, 2) };
                var name = Mono(f.Name, 12.5, f.Depth >= 2 ? Pal.Cyan : Pal.Ink); name.Margin = new Thickness(14 * f.Depth, 0, 0, 0);
                Control[] cells = [Mono(f.Tag, 12, Pal.Amber), name, Mono(f.Value, 12.5, Pal.Ink2)];
                for (int i = 0; i < 3; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
                fields.Children.Add(g);
            }
            var tree = new StackPanel();
            foreach (var n in r.SclTree)
                tree.Children.Add(Row(8, Mono(n.Kind.ToUpperInvariant(), 10.5, Pal.Ink3).WithWidth(84), Mono(n.Name, 12.5, Pal.Ink), Mono(n.Detail ?? "", 12, Pal.Cyan)).WithMargin(14 * n.Depth, 2, 0, 2));
            var chartTitle = kind == "sv" ? T("Merging-unit waveform", "Bentuk gelombang merging unit") : T("GOOSE retransmission", "Retransmisi GOOSE");
            Control lower = r.PhasorSvg is { } ph
                ? Columns("*,0.8*,*", Panel(chartTitle, SvgView(r.ChartSvg), "4 kHz"), Panel(T("Phasors", "Fasor"), SvgView(ph, 300), "V kV · I A"), Panel(T("SCL model", "Model SCL"), tree, "BayController.icd"))
                : Columns("*,*", Panel(chartTitle, SvgView(r.ChartSvg), "2 ms → 1 s"), Panel(T("SCL model", "Model SCL"), tree, "BayController.icd"));
            return Stack(16,
                Annunciator(r.Kpis),
                Columns("1.5*,*",
                    Panel(T("Frame bytes", "Byte frame"), Stack(8, dump, legend), $"{r.Frame.Length} B · EtherType {(kind == "sv" ? "88BA" : "88B8")}"),
                    Panel(T("Decoded", "Hasil dekode"), fields, r.RoundTripOk ? T("round trip identical", "bolak-balik identik") : T("round trip differs", "bolak-balik berbeda"))),
                lower);
        });
        ContentControl host = new();
        Control Bar()
        {
            var items = new List<Control> { Seg(T("Message", "Pesan"), [("goose", "GOOSE", true), ("sv", "Sampled Values", true)], kind, v => { kind = v; host.Content = Bar(); live.Refresh(); }) };
            if (kind == "goose")
            {
                items.Add(Seg(T("Breaker XCBR1", "Pemutus XCBR1"), [("open", T("Open (trip)", "Buka (trip)"), true), ("closed", T("Closed", "Tutup"), true)], open ? "open" : "closed",
                    v => { open = v == "open"; host.Content = Bar(); live.Refresh(); }));
                items.Add(Slider(T("Retransmission", "Retransmisi"), 0, 9, sq, 1, v => { sq = (int)v; live.Refresh(); }, "0"));
            }
            else
            {
                items.Add(Slider(T("Phase current", "Arus fasa"), 50, 1200, amps, 10, v => { amps = v; live.Refresh(); }, "0", " A"));
                items.Add(Slider(T("Power-factor angle", "Sudut faktor daya"), -30, 60, phi, 1, v => { phi = v; live.Refresh(); }, "0", "°"));
            }
            items.Add(Seg("802.1Q VLAN", [("on", T("Tagged", "Bertag"), true), ("off", T("Untagged", "Tanpa tag"), true)], vlan ? "on" : "off", v => { vlan = v == "on"; host.Content = Bar(); live.Refresh(); }));
            return Ui.Controls([.. items]);
        }
        host.Content = Bar();
        return Stack(16, host, live);
    }

    // ------------------------------------------------------------------ CIM

    private static Control Cim()
    {
        string cs = "case14";
        Live? live = null;
        live = new Live(() =>
        {
            var r = CimScenario.Run(cs, 46, Lang);
            var checks = r.Checks.Select(c => new object[] { T(c.Quantity), c.Original, c.Imported, c.Match ? State("✓", "ok") : State("≠", "alarm") });
            var profiles = r.Profiles.Select(p => new object[] { p.Profile, p.Objects.ToString(CultureInfo.InvariantCulture), F(p.Bytes / 1024.0, "0.0") });
            return Stack(16,
                Annunciator(r.Kpis),
                Columns("1.5*,*",
                    Code(r.Snippet, $"{r.Case}.xml · {T("first lines of", "baris pertama dari")} {r.TotalLines:N0}", "xml", 560),
                    Stack(16,
                        Panel(T("Round-trip check", "Pemeriksaan bolak-balik"), Table([T("Quantity", "Besaran"), T("Original", "Asli"), T("Imported", "Impor"), ""], checks, [1, 2]), "export → import → PF"),
                        Panel(T("CGMES profiles", "Profil CGMES"), Table([T("Profile", "Profil"), T("Objects", "Objek"), "KiB"], profiles, [1, 2]), "ExportProfiles"))),
                Panel(T("Objects by CIM class", "Objek per kelas CIM"), SvgView(r.ClassChartSvg), $"{r.Classes.Count} {T("classes", "kelas")}"));
        });
        ContentControl host = new();
        Control Bar() => Ui.Controls(Seg(T("Network", "Jaringan"), Cases(CimScenario.Cases), cs, v => { cs = v; host.Content = Bar(); live.Refresh(); }));
        host.Content = Bar();
        return Stack(16, host, live);
    }

    // ------------------------------------------------------------------ GIS

    private static Control Gis()
    {
        int size = 49; double scale = 1.25; bool cont = true;
        Live? live = null;
        live = new Live(() =>
        {
            var r = GisScenario.Run(size, scale, cont, Lang);
            var weakest = r.WeakestBuses.Select(w => new object[]
            {
                $"B{w.Bus}", Meter((w.Vm - 0.85) / 0.2 * 100, w.Vm < 0.95 ? "alarm" : w.Vm < 0.98 ? "warn" : "ok"), F(w.Vm, "0.000"),
            });
            return Stack(16,
                Annunciator(r.Kpis),
                Columns("1.6*,*",
                    Panel(T("Grid map", "Peta jaringan"), SvgView(r.MapSvg), "Web Mercator · West Java (synthetic)"),
                    Stack(16,
                        Panel(T("Weakest buses", "Bus terlemah"), Table(["Bus", "", "|V| pu"], weakest, [2]), "|V| pu"),
                        Code(r.GeoJsonSnippet, $"GeoJSON · {r.GeoJsonFeatures} features · {F(r.GeoJsonBytes / 1024.0, "0")} KiB", "json", 360))));
        });
        ContentControl host = new();
        Control Bar() => Ui.Controls(
            Seg(T("Substations", "Gardu induk"), GisScenario.Sizes.Select(s => (s.ToString(CultureInfo.InvariantCulture), s.ToString(CultureInfo.InvariantCulture), true)), size.ToString(CultureInfo.InvariantCulture),
                v => { size = int.Parse(v, CultureInfo.InvariantCulture); host.Content = Bar(); live.Refresh(); }),
            Slider(T("Load scale", "Skala beban"), 0.8, 1.8, scale, 0.05, v => { scale = v; live.Refresh(); }, "0.00", "×"),
            Seg(T("Colours", "Warna"), [("ramp", T("Continuous", "Kontinu"), true), ("bands", T("Bands", "Pita"), true)], cont ? "ramp" : "bands",
                v => { cont = v == "ramp"; host.Content = Bar(); live.Refresh(); }));
        host.Content = Bar();
        return Stack(16, host, live);
    }

    // ------------------------------------------------------------------ declarative demos

    /// <summary>Generic renderer for <see cref="IInteractiveScenario"/> demos (same contract as the web InteractiveView).</summary>
    private static Control Interactive(IInteractiveScenario scenario)
    {
        var values = InteractiveScenarios.Defaults(scenario);
        ContentControl host = new();
        Live? live = null;
        live = new Live(() =>
        {
            InteractiveResult r;
            try { r = scenario.Run(values, Lang); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or ArithmeticException)
            {
                return Notice(T("The study did not finish with these settings: ", "Studi tidak selesai dengan pengaturan ini: ") + e.Message);
            }
            var items = new List<Control> { Annunciator(r.Kpis) };
            Control? pending = null;
            foreach (var b in r.Blocks)
            {
                var c = RenderBlock(b);
                if (b.Wide)
                {
                    if (pending is not null) { items.Add(Columns("*,*", pending, new Border())); pending = null; }
                    items.Add(c);
                }
                else if (pending is null) pending = c;
                else { items.Add(Columns("*,*", pending, c)); pending = null; }
            }
            if (pending is not null) items.Add(Columns("*,*", pending, new Border()));
            return Stack(16, [.. items]);
        });
        Control Bar()
        {
            var controls = new List<Control>();
            foreach (var p in scenario.Params(values))
            {
                switch (p)
                {
                    case ChoiceParam c:
                        controls.Add(Seg(T(c.Label), c.Options.Select(o => (o.Value, T(o.Text), true)), values[c.Key], x =>
                        {
                            values[c.Key] = x;
                            InteractiveScenarios.Sync(scenario, values);
                            host.Content = Bar();
                            live.Refresh();
                        }));
                        break;
                    case RangeParam rp:
                        controls.Add(Slider(T(rp.Label), rp.Min, rp.Max, values.D(rp.Key), rp.Step, x =>
                        {
                            values[rp.Key] = x.ToString(CultureInfo.InvariantCulture);
                            live.Refresh();
                        }, rp.Format, rp.Unit));
                        break;
                }
            }
            return Ui.Controls([.. controls]);
        }
        host.Content = Bar();
        return Stack(16, host, live);
    }

    private static Control RenderBlock(Block b)
    {
        string? meta = b.Meta is { } m ? T(m) : null;
        switch (b)
        {
            case ChartBlock c:
                return Panel(T(c.Title), SvgView(c.Svg, 520), meta);
            case TableBlock t:
                var numeric = Enumerable.Range(0, t.Headers.Length).Where(i => t.Rows.Count > 0 && t.Rows.All(r => i < r.Length && r[i].Numeric)).ToArray();
                var rows = t.Rows.Select(r => r.Select(cell => cell.Tone is not null ? (object)State(cell.Text, cell.Tone) : cell.Text).ToArray());
                return Panel(T(t.Title), Table(t.Headers.Select(h => T(h)).ToArray(), rows, numeric, 16), meta);
            case ListingBlock l:
                return Code(l.Text, T(l.Title) + (meta is null ? "" : " · " + meta), l.Language == "text" ? "csharp" : l.Language, 360);
            case NoteBlock n:
                return Notice(T(n.Text));
            default:
                return new Border();
        }
    }

    // ------------------------------------------------------------------ AI agent

    private static Control Agent()
    {
        var agent = AgentScenario.CreateAgent();
        var log = new StackPanel { Spacing = 12 };
        var scroll = new ScrollViewer { Content = log, Height = 520 };
        bool busy = false;
        void Add(bool user, string text, IReadOnlyList<string>? tools = null, string? language = null)
        {
            var body = new StackPanel { Spacing = 4, HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left, MaxWidth = 720 };
            body.Children.Add(Eyebrow(user ? T("You", "Anda") : "Electrical.AI"));
            body.Children.Add(new Border
            {
                Background = user ? Pal.Ink : Pal.Panel2, BorderBrush = user ? Pal.Ink : Pal.Cyan, BorderThickness = new Thickness(user ? 0 : 3, 0, 0, 0),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9),
                Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = user ? Pal.Bg : Pal.Ink, FontFamily = Font(user ? "Body" : "Mono"), FontSize = user ? 14 : 12.5 },
            });
            if (tools is { Count: > 0 }) body.Children.Add(Mono("⚙ " + string.Join(" · ", tools) + (language is null ? "" : $" · {language}"), 11.5, Pal.Cyan));
            log.Children.Add(body);
            scroll.ScrollToEnd();
        }
        async Task Ask(string q)
        {
            if (busy || string.IsNullOrWhiteSpace(q)) return;
            busy = true;
            Add(true, q);
            var a = await AgentScenario.AskAsync(agent, q);
            Add(false, a.Text, a.ToolsUsed, a.Language);
            busy = false;
        }
        var first = AgentScenario.AskAsync(agent, T(AgentScenario.SuggestedPrompts[0])).GetAwaiter().GetResult();
        Add(true, T(AgentScenario.SuggestedPrompts[0]));
        Add(false, first.Text, first.ToolsUsed, first.Language);

        var input = new TextBox { PlaceholderText = T("Ask about the grid in English or Bahasa Indonesia…", "Tanyakan tentang jaringan dalam bahasa Inggris atau Indonesia…"), MinWidth = 300 };
        var send = new Button { Content = T("Ask", "Tanya"), Background = Pal.Ink, Foreground = Pal.Bg, Padding = new Thickness(16, 7), CornerRadius = new CornerRadius(6) };
        send.Click += async (_, _) => { var q = input.Text ?? ""; input.Text = ""; await Ask(q); };
        input.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { var q = input.Text ?? ""; input.Text = ""; await Ask(q); } };
        var askRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(send, Dock.Right); send.Margin = new Thickness(10, 0, 0, 0);
        askRow.Children.Add(send); askRow.Children.Add(input);

        var prompts = new StackPanel { Spacing = 6 };
        foreach (var p in AgentScenario.SuggestedPrompts)
            foreach (var (text, isId) in new[] { (p.En, false), (p.Id, true) })
            {
                var b = new Button
                {
                    Content = (isId ? "ID · " : "") + text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Pal.Panel2, Foreground = isId ? Pal.Ink2 : Pal.Ink, BorderBrush = Pal.Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), FontSize = 12.5,
                };
                b.Click += async (_, _) => await Ask(text);
                prompts.Children.Add(b);
            }
        var tools = new StackPanel { Spacing = 4 };
        foreach (var t in agent.Tools) tools.Children.Add(Mono(t.Name, 12, Pal.Cyan));
        var note = Text(AgentScenario.LlmConfigured
            ? T("An LLM endpoint is configured through ELECTRICAL_LLM_* variables.", "Endpoint LLM diatur melalui variabel ELECTRICAL_LLM_*.")
            : T("Running offline with the rule-based model. Set ELECTRICAL_LLM_* variables to use an LLM.", "Berjalan offline dengan model berbasis aturan. Atur variabel ELECTRICAL_LLM_* untuk memakai LLM."), 12.5, Pal.Ink3);
        return Columns("1.6*,*",
            Panel(T("Operator console", "Konsol operator"), Stack(12, scroll, askRow), $"{agent.Model.Name} · IEEE 14-bus"),
            Stack(16, Panel(T("Try a prompt", "Coba perintah"), prompts, "EN · ID"), Panel(T("Tools", "Alat"), tools, agent.Tools.Count.ToString(CultureInfo.InvariantCulture)), note));
    }
}

internal static class ControlExtensions
{
    public static T WithMargin<T>(this T c, double l, double t, double r, double b) where T : Control { c.Margin = new Thickness(l, t, r, b); return c; }
    public static T WithWidth<T>(this T c, double w) where T : Control { c.Width = w; return c; }
}
