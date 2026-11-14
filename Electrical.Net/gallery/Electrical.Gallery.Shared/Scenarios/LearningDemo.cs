using System.Diagnostics;
using System.Globalization;
using Electrical.AI;
using Electrical.AI.Learning;
using Electrical.Core.Data;
using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.PowerFlow;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// Machine learning for grids (Electrical.AI.Learning): load-forecast benchmark (GraviLearn gradient boosting and ML.NET FastTree vs.
/// Holt–Winters and regression), fault classification/location from IEC 60909 simulations (confusion matrix), topology identification of
/// a reconfigurable feeder, and an RLNet DQN Volt/VAR agent compared with rule-based and tabular policies. Every study is trained once per
/// process (small budgets) and cached.
/// </summary>
public sealed class LearningDemo : IInteractiveScenario
{
    public string Id => "learning";

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new ChoiceParam("view", new("Study", "Studi"),
            [("forecast", new("load forecasting", "peramalan beban")), ("faults", new("fault classification", "klasifikasi gangguan")),
             ("topology", new("topology identification", "identifikasi topologi")), ("voltvar", new("RL Volt/VAR", "RL Volt/VAR"))], "forecast"),
    ];

    // ------------------------------------------------------------------ cached studies

    private sealed record ForecastStudy(LoadForecastBenchmarkResult Result, DateTimeOffset TestStart, double Seconds);
    private sealed record FaultStudy(FaultLearningReport Report, double[,] Confusion, int[] HopHistogram, double Seconds, int Features);
    private sealed record TopologyStudy(TopologyIdentificationReport Report, (string Name, string Open, double LossesKw, double Clf, double Corr)[] Configs, double Seconds);
    private sealed record VoltVarStudy(VoltVarComparison Cmp, VoltVarEpisode None, VoltVarEpisode Rule, IReadOnlyList<string> ActionNames, double Seconds);

    private static readonly Lazy<ForecastStudy> Forecast = new(() =>
    {
        var sw = Stopwatch.StartNew();
        var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
        var temp = SyntheticProfiles.Temperature(start, 49, TimeSpan.FromHours(1), meanC: 28, dailyAmplitudeC: 4, noiseC: 1.0, seed: 21);
        var load = SyntheticProfiles.Load(LoadProfileKind.Residential, start, 49, TimeSpan.FromHours(1), peakMw: 10, noise: 0.01, seed: 22,
            temperature: temp, coolingPerDegree: 0.03);
        var r = LoadForecastBenchmark.Run(load, temp, new LoadForecastBenchmarkOptions { TestDays = 7 });
        return new ForecastStudy(r, load.TimeAt(r.TrainSamples), sw.Elapsed.TotalSeconds);
    });

    private static readonly Lazy<FaultStudy> Faults = new(() =>
    {
        var sw = Stopwatch.StartNew();
        var ds = FaultDatasetGenerator.Generate(new FaultDatasetOptions { Samples = 1200 });
        var (model, report) = FaultLearning.Train(ds, new ClassificationTrainingOptions { Trees = 40 });
        var (_, test) = ds.Split();
        var codes = FaultDatasetGenerator.Codes;
        var pred = model.Classifier.Predict(test.Select(s => s.Features).ToArray());
        var buses = model.Locator.Predict(test.Select(s => s.Features).ToArray());
        var cm = new double[codes.Count, codes.Count];
        var hist = new int[5];
        var hops = new Dictionary<int, int[]>();
        for (int i = 0; i < test.Count; i++)
        {
            cm[IndexOf(codes, test[i].Code), IndexOf(codes, pred[i])]++;
            int truth = test[i].NearestBusId;
            if (!hops.TryGetValue(truth, out var d)) hops[truth] = d = Topology.HopDistance(ds.Network, truth);
            int h = d[ds.Network.IndexOf(int.Parse(buses[i], CultureInfo.InvariantCulture))];
            hist[Math.Clamp(h < 0 ? 4 : h, 0, 4)]++;
        }
        return new FaultStudy(report, cm, hist, sw.Elapsed.TotalSeconds, ds.FeatureNames.Length);
    });

    private static int IndexOf(IReadOnlyList<string> list, string s) { for (int i = 0; i < list.Count; i++) if (list[i] == s) return i; return 0; }

    private static readonly Lazy<TopologyStudy> Topo = new(() =>
    {
        var sw = Stopwatch.StartNew();
        var o = new TopologySnapshotOptions { SnapshotsPerConfiguration = 40 };
        var snaps = TopologyIdentification.GenerateSnapshots(o);
        var (clf, corr, report) = TopologyIdentification.Train(snaps, o, new ClassificationTrainingOptions { Trees = 40 });
        // same held-out split as TopologyIdentification.Train (test fraction 0.3, seed 3)
        var idx = Enumerable.Range(0, snaps.Count).ToArray();
        new Random(3).Shuffle(idx);
        var test = idx.Take((int)Math.Round(snaps.Count * 0.3)).Select(i => snaps[i]).ToArray();
        var pc = clf.Predict(test.Select(s => s.Features).ToArray());
        var net = ReconfigurableFeeders.Case33WithTies();
        var configs = o.Configurations.Select(c =>
        {
            var mine = Enumerable.Range(0, test.Length).Where(i => test[i].Configuration == c.Name).ToArray();
            double a1 = mine.Length == 0 ? double.NaN : mine.Count(i => pc[i] == c.Name) / (double)mine.Length;
            double a2 = mine.Length == 0 ? double.NaN : mine.Count(i => corr.Identify(test[i].Features) == c.Name) / (double)mine.Length;
            double loss = PowerFlowSolver.Solve(c.Apply(net)).LossesMw * 1000;
            return (c.Name, string.Join(" ", c.OpenBranches.Select(b => $"{b.From}-{b.To}")), loss, a1, a2);
        }).ToArray();
        return new TopologyStudy(report, configs, sw.Elapsed.TotalSeconds);
    });

    private static readonly Lazy<VoltVarStudy> VoltVar = new(() =>
    {
        var sw = Stopwatch.StartNew();
        var cmp = VoltVarAgentTrainer.Compare(options: new VoltVarRlOptions { Steps = 6_000 });
        var env = new VoltVarEnvironment();
        var none = VoltVarPolicies.Evaluate(env, VoltVarPolicies.DoNothing, 1000);
        var rule = VoltVarPolicies.Evaluate(env, VoltVarPolicies.VoltageRule, 1000);
        var names = Enumerable.Range(0, env.ActionCount).Select(env.DescribeAction).ToArray();
        return new VoltVarStudy(cmp, none, rule, names, sw.Elapsed.TotalSeconds);
    });

    // ------------------------------------------------------------------ views

    public InteractiveResult Run(ParamValues v, string lang) => v.Get("view", "forecast") switch
    {
        "faults" => FaultView(lang),
        "topology" => TopologyView(lang),
        "voltvar" => VoltVarView(lang),
        _ => ForecastView(lang),
    };

    private static string Name(string key, string lang) => key switch
    {
        "GraviLearn gradient boosting" => Pick(lang, "GraviLearn boosting", "GraviLearn boosting"),
        "Regression" => Pick(lang, "regression", "regresi"),
        _ => key,
    };

    private static InteractiveResult ForecastView(string lang)
    {
        var s = Forecast.Value;
        var r = s.Result;
        var order = r.Mape.OrderBy(kv => kv.Value).ToList();
        string[] colors = ["eln-accent", "eln-ok", "eln-amber", "eln-danger"];
        int show = 3 * 24;
        var series = new List<ChartSeries> { new(Pick(lang, "actual", "aktual"), r.Actual.Take(show).ToArray(), SvgTheme.Var("eln-fg")) { StrokeWidth = 2.6 } };
        int ci = 0;
        foreach (var (k, _) in order) series.Add(new ChartSeries(Name(k, lang), r.Forecasts[k].Take(show).ToArray(), SvgTheme.Var(colors[ci++ % colors.Length])) { Dashed = k == "Holt-Winters" });
        string line = SvgCharts.Line(series, new ChartOptions { Width = 760, Height = 290, YLabel = "MW", TimeStart = s.TestStart, TimeStep = TimeSpan.FromHours(1) });
        string bars = SvgCharts.Bar(order.Select(kv => Name(kv.Key, lang)).ToList(), [new ChartSeries("MAPE %", order.Select(kv => kv.Value).ToArray(), SvgTheme.Var("eln-accent"))],
            new ChartOptions { Width = 520, Height = 260, YLabel = "MAPE [%]", ShowLegend = false, ValueFormat = "0.00" });
        var best = order[0];
        double hw = r.Mape["Holt-Winters"];
        var rows = order.Select((kv, i) => new Cell[]
        {
            new((i + 1).ToString(CultureInfo.InvariantCulture), null, true), Name(kv.Key, lang), Cell.Num(kv.Value, "0.00"),
            Cell.Num(r.Forecasts[kv.Key].Zip(r.Actual).Max(p => Math.Abs(p.First - p.Second)), "0.00"),
            kv.Key is "Holt-Winters" or "Regression" ? Pick(lang, "baseline", "acuan") : "ML",
        }).ToList();
        return new InteractiveResult(
        [
            new(new("Best model", "Model terbaik"), Name(best.Key, lang), null, Same($"MAPE {F(best.Value, "0.00")} %"), "ok"),
            new(new("Holt-Winters", "Holt-Winters"), F(hw, "0.00"), "%", new("cannot see the hot days coming", "tidak bisa melihat hari panas"), "warn"),
            new(new("Improvement", "Perbaikan"), F(hw / best.Value, "0.0"), "×", new("lower error than the baseline", "galat lebih rendah dari acuan"), "ok"),
            new(new("Training set", "Data latih"), r.TrainSamples.ToString(CultureInfo.InvariantCulture), Pick(lang, "hours", "jam"), Same($"{r.TestDays} {Pick(lang, "test days", "hari uji")} · {F(s.Seconds, "0.0")} s")),
        ],
        [
            new ChartBlock(new("Day-ahead forecasts vs. actual (first 3 test days)", "Ramalan sehari ke depan vs. aktual (3 hari uji pertama)"), line,
                new("rolling origin at midnight, temperature forecast known", "titik awal bergulir tiap tengah malam, ramalan suhu diketahui"), Wide: true),
            new ChartBlock(new("MAPE over 7 test days", "MAPE selama 7 hari uji"), bars, Same("LoadForecastBenchmark.Run")),
            new TableBlock(new("Ranking", "Peringkat"), [Same("#"), new("Model", "Model"), Same("MAPE %"), new("Max error MW", "Galat maks MW"), new("Kind", "Jenis")], rows,
                new("features: lags 1/24/168 h, calendar, holiday, temperature", "fitur: lag 1/24/168 jam, kalender, libur, suhu")),
            new NoteBlock(new("Residential load whose peak grows 3 % per °C above 24 °C, with day-to-day weather swings. Holt–Winters only extrapolates the daily and weekly seasons; the tree ensembles (GraviLearn gradient boosting, ML.NET FastTree) learn the temperature response from lag, calendar and weather features and forecast 24 h recursively.",
                "Beban rumah tangga yang puncaknya naik 3 % per °C di atas 24 °C, dengan cuaca yang berubah dari hari ke hari. Holt–Winters hanya mengekstrapolasi musim harian dan mingguan; ensambel pohon (gradient boosting GraviLearn, FastTree ML.NET) mempelajari respons suhu dari fitur lag, kalender, dan cuaca, lalu meramal 24 jam secara rekursif.")),
        ]);
    }

    private static InteractiveResult FaultView(string lang)
    {
        var s = Faults.Value;
        var r = s.Report;
        var codes = FaultDatasetGenerator.Codes.ToList();
        string heat = SvgCharts.Heatmap(s.Confusion, codes, codes, new HeatmapOptions { CellWidth = 44, CellHeight = 26, ShowValues = true, ValueFormat = "0", Ramp = ColorRamp.Thermal });
        L[] hopLabels = [new("exact bus", "bus tepat"), new("1 bus away", "selisih 1 bus"), new("2 buses", "2 bus"), new("3 buses", "3 bus"), new("≥ 4", "≥ 4")];
        string hops = SvgCharts.Bar(hopLabels.Select(l => l[lang]).ToList(),
            [new ChartSeries(Pick(lang, "test samples", "sampel uji"), s.HopHistogram.Select(x => (double)x).ToArray(), SvgTheme.Var("eln-accent"))],
            new ChartOptions { Width = 520, Height = 240, YLabel = Pick(lang, "samples", "sampel"), ShowLegend = false, ValueFormat = "0" });
        var perClass = codes.Select((c, i) =>
        {
            double total = Enumerable.Range(0, codes.Count).Sum(j => s.Confusion[i, j]);
            return new Cell[] { c, FaultDatasetGenerator.TypeOf(c).ToString(), new(total.ToString(CultureInfo.InvariantCulture), null, true), Cell.Num(total == 0 ? double.NaN : 100 * s.Confusion[i, i] / total, "0.0") };
        }).ToList();
        return new InteractiveResult(
        [
            new(new("Fault type", "Jenis gangguan"), F(r.TypeAccuracy * 100, "0.0"), "%", new("3φ / SLG / LL / DLG", "3φ / SLG / LL / DLG"), r.TypeAccuracy > 0.95 ? "ok" : "warn"),
            new(new("Phase code", "Kode fasa"), F(r.CodeAccuracy * 100, "0.0"), "%", new("10 classes AG … ABC", "10 kelas AG … ABC"), "ok"),
            new(new("Location ±1 bus", "Lokasi ±1 bus"), F(r.LocationWithinOneHop * 100, "0.0"), "%", Same($"{Pick(lang, "exact", "tepat")} {F(r.LocationAccuracy * 100, "0.0")} %"), r.LocationWithinOneHop > 0.85 ? "ok" : "warn"),
            new(new("Dataset", "Dataset"), (r.TrainSamples + r.TestSamples).ToString(CultureInfo.InvariantCulture), Pick(lang, "faults", "gangguan"),
                Same($"{s.Features} {Pick(lang, "features", "fitur")} · {F(s.Seconds, "0.0")} s")),
        ],
        [
            new ChartBlock(new("Confusion matrix, phase code (rows = true, columns = predicted)", "Matriks kebingungan, kode fasa (baris = benar, kolom = prediksi)"), heat,
                Same($"{r.TestSamples} {Pick(lang, "held-out faults", "gangguan uji")} · random forest")),
            new ChartBlock(new("Location error in bus hops", "Galat lokasi dalam lompatan bus"), hops, new("nearest-bus classifier", "klasifikasi bus terdekat")),
            new TableBlock(new("Recall per class", "Recall per kelas"), [new("Code", "Kode"), new("Type", "Jenis"), new("Test", "Uji"), Same("Recall %")], perClass,
                Same("FaultLearning.Train · FaultDatasetGenerator (Case33BW)"), Wide: true),
            new NoteBlock(new("Training data come from IEC 60909 short-circuit studies on the Baran–Wu 33-bus feeder: bus faults and faults at 25/50/75 % of a line, all ten phase combinations, fault resistance up to 5 Ω and 0.5 % measurement noise, seen only through 7 voltage and 5 current measurements. The location model predicts the nearest bus.",
                "Data latih berasal dari studi hubung singkat IEC 60909 pada penyulang Baran–Wu 33 bus: gangguan di bus dan pada 25/50/75 % saluran, sepuluh kombinasi fasa, resistansi gangguan hingga 5 Ω, dan derau pengukuran 0,5 %, yang hanya terlihat melalui 7 pengukuran tegangan dan 5 arus. Model lokasi memprediksi bus terdekat.")),
        ]);
    }

    private static InteractiveResult TopologyView(string lang)
    {
        var s = Topo.Value;
        var r = s.Report;
        var names = s.Configs.Select(c => c.Name).ToList();
        string bars = SvgCharts.Bar(names,
        [
            new ChartSeries(Pick(lang, "random forest", "random forest"), s.Configs.Select(c => c.Clf * 100).ToArray(), SvgTheme.Var("eln-accent")),
            new ChartSeries(Pick(lang, "correlation", "korelasi"), s.Configs.Select(c => c.Corr * 100).ToArray(), SvgTheme.Var("eln-amber")),
        ], new ChartOptions { Width = 640, Height = 260, YLabel = Pick(lang, "accuracy [%]", "akurasi [%]"), YMin = 0, YMax = 100, ValueFormat = "0" });
        double minLoss = s.Configs.Min(c => c.LossesKw);
        var rows = s.Configs.Select(c => new Cell[]
        {
            c.Name, c.Open, Cell.Num(c.LossesKw, "0.0"),
            c.LossesKw <= minLoss + 1e-9 ? Cell.Chip(Pick(lang, "min loss", "rugi min"), "ok") : Cell.Chip("", "na"),
            Cell.Num(c.Clf * 100, "0"), Cell.Num(c.Corr * 100, "0"),
        }).ToList();
        return new InteractiveResult(
        [
            new(new("Classifier", "Klasifikasi"), F(r.ClassifierAccuracy * 100, "0.0"), "%", new("random forest on voltage signatures", "random forest pada tanda tegangan"), r.ClassifierAccuracy > 0.95 ? "ok" : "warn"),
            new(new("Correlation baseline", "Acuan korelasi"), F(r.CorrelationAccuracy * 100, "0.0"), "%", new("no training beyond mean signatures", "tanpa pelatihan selain tanda rata-rata"), r.CorrelationAccuracy > 0.95 ? "ok" : "warn"),
            new(new("Configurations", "Konfigurasi"), r.Configurations.ToString(CultureInfo.InvariantCulture), null, Same($"{r.TrainSamples} / {r.TestSamples} {Pick(lang, "train / test", "latih / uji")}")),
            new(new("Min-loss configuration", "Konfigurasi rugi minimum"), F(minLoss, "0.0"), "kW", new("Goswami & Basu: 139.5 kW", "Goswami & Basu: 139,5 kW"), "ok"),
        ],
        [
            new ChartBlock(new("Held-out accuracy per switching configuration", "Akurasi uji per konfigurasi pensaklaran"), bars,
                Same($"TopologyIdentification.Train · {F(s.Seconds, "0.0")} s"), Wide: true),
            new TableBlock(new("Radial configurations of the 33-bus feeder with 5 tie switches", "Konfigurasi radial penyulang 33 bus dengan 5 saklar penghubung"),
                [new("Configuration", "Konfigurasi"), new("Open branches", "Cabang terbuka"), new("Losses kW", "Rugi kW"), Same(""), Same("RF %"), new("Corr. %", "Korel. %")], rows,
                Same("ReconfigurableFeeders.Case33WithTies()"), Wide: true),
            new NoteBlock(new("Snapshots of 9 bus voltages plus the substation flow are simulated for each radial configuration at load levels between 60 % and 120 % with 10 % per-load noise. On a radial feeder the normalized voltage-drop profile barely depends on the load level, so both the classifier and the correlation baseline recognise the switch state.",
                "Snapshot 9 tegangan bus ditambah aliran gardu induk disimulasikan untuk setiap konfigurasi radial pada tingkat beban 60 %–120 % dengan derau 10 % per beban. Pada penyulang radial, profil jatuh tegangan ternormalisasi hampir tidak bergantung pada tingkat beban, sehingga klasifikasi maupun acuan korelasi mengenali keadaan saklar.")),
        ]);
    }

    private static InteractiveResult VoltVarView(string lang)
    {
        var s = VoltVar.Value;
        var c = s.Cmp;
        (string Name, double Reward)[] policies =
        [
            (Pick(lang, "do nothing", "tanpa aksi"), c.DoNothing), (Pick(lang, "tabular Q", "Q tabular"), c.TabularQLearning),
            (Pick(lang, "voltage rule", "aturan tegangan"), c.VoltageRule), ($"RLNet {c.DeepRlName}", c.DeepRl),
        ];
        string bars = SvgCharts.Bar(policies.Select(p => p.Name).ToList(), [new ChartSeries(Pick(lang, "episode reward", "imbalan episode"), policies.Select(p => p.Reward).ToArray(), SvgTheme.Var("eln-accent"))],
            new ChartOptions { Width = 560, Height = 260, YLabel = Pick(lang, "reward (higher is better)", "imbalan (makin tinggi makin baik)"), ShowLegend = false, ValueFormat = "0.0" });
        var acts = c.DeepRlEpisode.Actions;
        string actSvg = SvgCharts.Line([new ChartSeries(Pick(lang, "action", "aksi"), acts.Select(a => (double)a).ToArray(), SvgTheme.Var("eln-amber")) { X = Enumerable.Range(0, acts.Count).Select(h => (double)h).ToArray() }],
            new ChartOptions { Width = 560, Height = 260, XLabel = Pick(lang, "hour", "jam"), YLabel = Pick(lang, "action index", "indeks aksi"), ShowLegend = false, YMin = 0, YMax = s.ActionNames.Count - 1, ValueFormat = "0" });
        VoltVarEpisode dq = c.DeepRlEpisode;
        Cell[] Row(string n, VoltVarEpisode e) => [n, Cell.Num(e.TotalReward, "0.0"), Cell.Num(e.EnergyLossesKwh, "0"), new(e.ViolationBusHours.ToString(CultureInfo.InvariantCulture), null, true), Cell.Num(e.MinVoltagePu, "0.000")];
        var actionRows = s.ActionNames.Select((n, i) => new Cell[] { new(i.ToString(CultureInfo.InvariantCulture), null, true), n, new(acts.Count(a => a == i).ToString(CultureInfo.InvariantCulture), null, true) }).ToList();
        return new InteractiveResult(
        [
            new(Same($"RLNet {c.DeepRlName}"), F(c.DeepRl, "0.0"), null, Same($"6 000 {Pick(lang, "steps", "langkah")} · {F(c.DeepRlTrainingSeconds, "0.0")} s"), c.DeepRl > c.VoltageRule ? "ok" : "warn"),
            new(new("Voltage rule", "Aturan tegangan"), F(c.VoltageRule, "0.0"), null, new("hand-written controller", "pengendali buatan tangan")),
            new(new("Tabular Q-learning", "Q-learning tabular"), F(c.TabularQLearning, "0.0"), null, new("400 episodes, discretised state", "400 episode, keadaan didiskretkan")),
            new(new("Do nothing", "Tanpa aksi"), F(c.DoNothing, "0.0"), null, Same($"{s.None.ViolationBusHours} {Pick(lang, "violation bus-hours", "bus-jam pelanggaran")}"), "alarm"),
        ],
        [
            new ChartBlock(new("Average episode reward (24 h)", "Imbalan episode rata-rata (24 jam)"), bars, Same("VoltVarAgentTrainer.Compare")),
            new ChartBlock(new("Greedy DQN actions over the day", "Aksi DQN serakah sepanjang hari"), actSvg, new("see the action table below", "lihat tabel aksi di bawah")),
            new TableBlock(new("Episode metrics", "Metrik episode"), [new("Policy", "Kebijakan"), new("Reward", "Imbalan"), new("Losses kWh", "Rugi kWh"), new("Violations", "Pelanggaran"), Same("Vmin pu")],
                [Row(Pick(lang, "do nothing", "tanpa aksi"), s.None), Row(Pick(lang, "voltage rule", "aturan tegangan"), s.Rule), Row($"RLNet {c.DeepRlName}", dq)],
                new("violation = bus outside 0.95–1.05 pu in an hour", "pelanggaran = bus di luar 0,95–1,05 pu dalam satu jam")),
            new TableBlock(new("Action space", "Ruang aksi"), [Same("#"), new("Action", "Aksi"), new("Used (h)", "Dipakai (jam)")], actionRows, Same("VoltVarEnvironment.DescribeAction")),
            new NoteBlock(new($"The agent controls tap changers and capacitor banks of the 33-bus feeder hour by hour; the reward penalises losses, voltage violations and switching. Training (Gravicode.RLNet DQN, 64×64 network, ε-greedy, fixed seed) took {F(s.Seconds, "0.0")} s including the baselines, once per process. A longer budget (48 000 steps) reaches −13.8 for both DQN and PPO.",
                $"Agen mengendalikan pengubah tap dan bank kapasitor penyulang 33 bus jam demi jam; imbalan memberi penalti pada rugi, pelanggaran tegangan, dan pensaklaran. Pelatihan (Gravicode.RLNet DQN, jaringan 64×64, ε-greedy, seed tetap) memakan {F(s.Seconds, "0.0")} s termasuk acuan, sekali per proses. Anggaran lebih panjang (48 000 langkah) mencapai −13,8 untuk DQN maupun PPO.")),
        ]);
    }

    /// <summary>Starts training the cached studies in the background (call once at app start-up so the pages open instantly).</summary>
    public static void Warm() => Task.Run(() => { _ = Forecast.Value; _ = Faults.Value; _ = Topo.Value; _ = VoltVar.Value; });
}
