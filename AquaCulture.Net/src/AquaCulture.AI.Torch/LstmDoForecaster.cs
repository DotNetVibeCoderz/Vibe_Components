using AquaCulture.AI.Learning;
using AquaCulture.Core.Hosting;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;

namespace AquaCulture.AI.Torch;

/// <summary>
/// LSTM DO forecaster (TorchSharp / libtorch): a sequence of the last <see cref="Lookback"/> hours of DO, water temperature, solar radiation,
/// aerator state and time of day goes through an LSTM; a linear head emits the next <see cref="Horizon"/> hours at once. Like
/// <see cref="GbtDoForecaster"/>, the last 20 % of the history is held out for <see cref="Skill"/> and the 90 % band.
/// Add a libtorch backend package to the application (<c>TorchSharp-cpu</c>, or <c>TorchSharp-cuda-windows</c> / <c>-linux</c>).
/// </summary>
public sealed class LstmDoForecaster : IDoForecaster, IDisposable
{
    public const int Inputs = 6;

    private sealed class Net : nn.Module<Tensor, Tensor>
    {
        private readonly LSTM _lstm;
        private readonly Linear _head;

        public Net(int hidden, int horizon) : base("aqua_do_lstm")
        {
            _lstm = nn.LSTM(Inputs, hidden, numLayers: 1, batchFirst: true);
            _head = nn.Linear(hidden, horizon);
            RegisterComponents();
        }

        public override Tensor forward(Tensor x)
        {
            var (output, _, _) = _lstm.call(x);
            using var last = output.select(1, -1);
            return _head.call(last);
        }
    }

    private readonly List<DoSkill> _skill = [];
    private Net? _net;

    public LstmDoForecaster(int horizon = 12, int lookback = 24, int hidden = 32, int epochs = 40, double learningRate = 0.01, int batchSize = 64, long seed = 7)
    {
        Horizon = horizon; Lookback = lookback; Hidden = hidden; Epochs = epochs; LearningRate = learningRate; BatchSize = batchSize; Seed = seed;
    }

    public string Name => "LSTM (TorchSharp)";
    public int Horizon { get; }
    public int Lookback { get; }
    public int Hidden { get; }
    public int Epochs { get; }
    public double LearningRate { get; }
    public int BatchSize { get; }
    public long Seed { get; }
    public IReadOnlyList<DoSkill> Skill => _skill;
    public IReadOnlyList<double> TrainingLoss { get; private set; } = [];

    /// <summary>Scaled input features of one hour.</summary>
    private static void Encode(DoObservation o, Span<float> f)
    {
        double hour = o.Time.Hour + o.Time.Minute / 60.0;
        f[0] = (float)(o.Do / 10);
        f[1] = (float)((double.IsNaN(o.WaterTempC) ? 29 : o.WaterTempC) - 29) / 3;
        f[2] = (float)(o.SolarWm2 / 1000);
        f[3] = o.AeratorOn ? 1 : 0;
        f[4] = (float)Math.Sin(2 * Math.PI * hour / 24);
        f[5] = (float)Math.Cos(2 * Math.PI * hour / 24);
    }

    private (Tensor X, Tensor Y) Windows(IReadOnlyList<DoObservation> h, int from, int to)
    {
        var starts = new List<int>();
        for (int t = Math.Max(from, Lookback - 1); t + Horizon < to; t++) starts.Add(t);
        var x = new float[starts.Count * Lookback * Inputs];
        var y = new float[starts.Count * Horizon];
        for (int i = 0; i < starts.Count; i++)
        {
            int t = starts[i];
            for (int k = 0; k < Lookback; k++) Encode(h[t - Lookback + 1 + k], x.AsSpan((i * Lookback + k) * Inputs, Inputs));
            for (int j = 0; j < Horizon; j++) y[i * Horizon + j] = (float)(h[t + 1 + j].Do / 10);
        }
        return (tensor(x, [starts.Count, Lookback, Inputs]), tensor(y, [starts.Count, Horizon]));
    }

    public LstmDoForecaster Train(IReadOnlyList<DoObservation> history)
    {
        if (history.Count < Lookback + Horizon + 48) throw new ArgumentException("Need at least three days of hourly history.");
        int split = (int)(history.Count * 0.8);
        // validation pass on the first 80 %, then a final model on everything
        using (var validation = Fit(history, split))
        {
            _skill.Clear();
            var (xv, yv) = Windows(history, split, history.Count);
            using (xv) using (yv) using (no_grad())
            {
                using var pred = validation.call(xv);
                var p = pred.data<float>().ToArray(); var y = yv.data<float>().ToArray();
                int n = (int)yv.shape[0];
                for (int h = 1; h <= Horizon; h++)
                {
                    var resid = new List<double>(); double se = 0;
                    for (int i = 0; i < n; i++) { double e = (y[i * Horizon + h - 1] - p[i * Horizon + h - 1]) * 10; resid.Add(e); se += e * e; }
                    var (pers, diurnal) = DoBaselines.Rmse(history, Math.Max(split, Lookback - 1), history.Count, h);
                    _skill.Add(new DoSkill(h, Math.Round(Math.Sqrt(se / Math.Max(1, n)), 4), Math.Round(pers, 4), Math.Round(diurnal, 4),
                        Math.Round(DoBaselines.Quantile(resid, 0.05), 4), Math.Round(DoBaselines.Quantile(resid, 0.95), 4)));
                }
            }
        }
        _net?.Dispose();
        _net = Fit(history, history.Count);
        return this;
    }

    private Net Fit(IReadOnlyList<DoObservation> h, int to)
    {
        torch.random.manual_seed(Seed);
        var net = new Net(Hidden, Horizon);
        var (x, y) = Windows(h, 0, to);
        using (x) using (y)
        {
            using var opt = optim.Adam(net.parameters(), LearningRate);
            using var lossFn = nn.MSELoss();
            long n = x.shape[0];
            var gen = new AquaCulture.Core.Data.DeterministicRandom((ulong)Seed);
            var losses = new List<double>();
            for (int epoch = 0; epoch < Epochs; epoch++)
            {
                var order = Enumerable.Range(0, (int)n).ToArray();
                for (int i = order.Length - 1; i > 0; i--) { int j = gen.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
                double sum = 0; int batches = 0;
                for (int b = 0; b < n; b += BatchSize)
                {
                    using var scope = NewDisposeScope();
                    var idx = tensor(order.Skip(b).Take(BatchSize).Select(v => (long)v).ToArray());
                    var xb = x.index_select(0, idx); var yb = y.index_select(0, idx);
                    opt.zero_grad();
                    var loss = lossFn.call(net.call(xb), yb);
                    loss.backward();
                    opt.step();
                    sum += loss.item<float>(); batches++;
                }
                losses.Add(sum / Math.Max(1, batches));
            }
            if (to == h.Count) TrainingLoss = losses;
        }
        net.eval();
        return net;
    }

    public DoForecastCurve Forecast(IReadOnlyList<DoObservation> recent)
    {
        var net = _net ?? throw new InvalidOperationException("Train or load the forecaster first.");
        if (recent.Count < Lookback) throw new ArgumentException($"Need {Lookback} hours of history.");
        var x = new float[Lookback * Inputs];
        for (int k = 0; k < Lookback; k++) Encode(recent[recent.Count - Lookback + k], x.AsSpan(k * Inputs, Inputs));
        float[] p;
        using (no_grad())
        using (var xt = tensor(x, [1, Lookback, Inputs]))
        using (var y = net.call(xt)) p = y.data<float>().ToArray();
        var last = recent[^1].Time;
        var times = Enumerable.Range(1, Horizon).Select(h => last.AddHours(h)).ToList();
        var pt = p.Select(v => Math.Round(Math.Max(0, v * 10.0), 3)).ToArray();
        var lo = pt.Select((v, i) => Math.Round(Math.Max(0, v + (_skill.Count > i ? _skill[i].P05Residual : 0)), 3)).ToArray();
        var hi = pt.Select((v, i) => Math.Round(v + (_skill.Count > i ? _skill[i].P95Residual : 0), 3)).ToArray();
        return new DoForecastCurve(Name, times, pt, lo, hi);
    }

    /// <summary>Saves the weights (TorchSharp format; the skill table is not kept — re-validate after loading if needed).</summary>
    public void Save(string path) => (_net ?? throw new InvalidOperationException("No model.")).save(path);

    public LstmDoForecaster Load(string path)
    {
        _net?.Dispose();
        _net = new Net(Hidden, Horizon);
        _net.load(path);
        _net.eval();
        return this;
    }

    public void Dispose() => _net?.Dispose();
}

public static class TorchBuilderExtensions
{
    /// <summary>Marks the TorchSharp forecasters as available (models are trained per pond, so nothing is registered).</summary>
    public static AquaBuilder AddTorchForecasting(this AquaBuilder b) => b.TryAddModule("AI.Torch", _ => { });
}
