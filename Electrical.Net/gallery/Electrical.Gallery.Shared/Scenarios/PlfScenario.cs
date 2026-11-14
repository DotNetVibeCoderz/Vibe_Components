using Electrical.Core.Data;
using Electrical.HPC;
using Electrical.Native;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;

namespace Electrical.Gallery.Shared;

public sealed record PlfDemoResult(
    string Case, int Samples, double LoadStd, ProbabilisticLoadFlowResult Result, string LossHistogramSvg, string VminHistogramSvg,
    string VoltageBandSvg, string Backend, bool NativeAvailable, IReadOnlyList<Kpi> Kpis);

/// <summary>Monte-Carlo probabilistic load flow (Electrical.HPC) with histograms.</summary>
public static class PlfScenario
{
    public static readonly string[] Cases = ["case33bw", "case14", "case9", "synthetic"];

    public static PlfDemoResult Run(string caseKey = "case33bw", int samples = 1000, double loadStd = 0.10, string lang = "en")
    {
        var net = GalleryCases.Load(caseKey);
        var r = ProbabilisticLoadFlow.Run(net, new ProbabilisticLoadFlowOptions { Samples = samples, LoadStdFraction = loadStd, SystemStdFraction = loadStd / 2 });
        bool id = lang == "id";
        bool kw = net.TotalLoadMw < 20;
        double lossScale = kw ? 1000 : 1;
        string lossUnit = kw ? "kW" : "MW";

        string lossSvg = Histogram(r.LossesMw.Select(x => x * lossScale).ToArray(), 24, id ? $"rugi-rugi ({lossUnit})" : $"losses ({lossUnit})", SvgTheme.Var("eln-accent"), id, kw ? "0" : "0.00");
        string vminSvg = Histogram(r.MinVoltage, 24, id ? "tegangan minimum (pu)" : "minimum voltage (pu)", SvgTheme.Var("eln-amber"), id, "0.000");

        var buses = r.Buses;
        string band = SvgCharts.Line(
        [
            new ChartSeries("P95", buses.Select(b => b.P95).ToList(), SvgTheme.Var("eln-grid")) { Dashed = true, StrokeWidth = 1.4 },
            new ChartSeries(id ? "rata-rata" : "mean", buses.Select(b => b.Mean).ToList(), SvgTheme.Var("eln-accent")) { StrokeWidth = 2.4 },
            new ChartSeries("P05", buses.Select(b => b.P05).ToList(), SvgTheme.Var("eln-amber")) { Dashed = true, StrokeWidth = 1.6 },
        ], new ChartOptions
        {
            Width = 640, Height = 250, YLabel = "|V| (pu)", XLabel = "bus", XLabels = buses.Select(b => b.BusId.ToString()).ToList(),
            ReferenceLines = [(0.95, "0.95")],
        });

        double pUnder = r.ProbabilityAnyUnderVoltage();
        var worstBus = buses.MaxBy(b => b.ProbabilityUnderVoltage)!;
        double p95Loss = Stats.Percentile(r.LossesMw, 0.95) * lossScale;
        var kpis = new List<Kpi>
        {
            new(new("Samples", "Sampel"), $"{r.Converged:N0}", $"/ {r.Samples:N0}", new($"{F(r.Elapsed.TotalMilliseconds, "0")} ms · {F(r.Converged / Math.Max(r.Elapsed.TotalSeconds, 1e-6), "0")} PF/s", $"{F(r.Elapsed.TotalMilliseconds, "0")} md · {F(r.Converged / Math.Max(r.Elapsed.TotalSeconds, 1e-6), "0")} PF/dtk")),
            new(new("Mean losses", "Rugi rata-rata"), F(r.MeanLossesMw * lossScale, kw ? "0.0" : "0.00"), lossUnit, new($"P95 {F(p95Loss, kw ? "0.0" : "0.00")} {lossUnit}", $"P95 {F(p95Loss, kw ? "0.0" : "0.00")} {lossUnit}")),
            new(new("P(Vmin < 0.95)", "P(Vmin < 0,95)"), F(100 * pUnder, "0.0"), "%", new($"worst bus {worstBus.BusId}", $"bus terburuk {worstBus.BusId}"), pUnder > 0.5 ? "alarm" : pUnder > 0.05 ? "warn" : "ok"),
            new(new("Sampling kernel", "Kernel sampling"), GridKernels.IsNativeAvailable ? "Rust" : "Managed", null, new(r.Backend, r.Backend), GridKernels.IsNativeAvailable ? "ok" : null),
        };
        return new PlfDemoResult(caseKey, samples, loadStd, r, lossSvg, vminSvg, band, r.Backend, GridKernels.IsNativeAvailable, kpis);
    }

    /// <summary>Histogram as a bar chart with bin-centre labels.</summary>
    public static string Histogram(double[] data, int bins, string xLabel, string color, bool id, string fmt)
    {
        if (data.Length == 0) return SvgCharts.Bar(["—"], [new ChartSeries("n", [0])]);
        double lo = data.Min(), hi = data.Max();
        if (hi - lo < 1e-12) { lo -= 0.5; hi += 0.5; }
        double w = (hi - lo) / bins;
        var counts = new double[bins];
        foreach (var x in data) counts[Math.Clamp((int)((x - lo) / w), 0, bins - 1)]++;
        var labels = Enumerable.Range(0, bins).Select(i => F(lo + (i + 0.5) * w, fmt)).ToList();
        return SvgCharts.Bar(labels, [new ChartSeries(id ? "jumlah" : "count", counts, color)], new ChartOptions
        {
            Width = 640, Height = 240, XLabel = xLabel, YLabel = id ? "jumlah sampel" : "samples", ShowLegend = false, ValueFormat = "0",
        });
    }
}
