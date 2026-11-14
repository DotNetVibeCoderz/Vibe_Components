using System.Numerics;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CoreBranchKind = Electrical.Core.Network.BranchKind;

namespace Electrical.Distribution;

/// <summary>Positive-sequence reduction of a <see cref="DistributionNetwork"/> to a Core <see cref="PowerNetwork"/>.</summary>
public static class BalancedReduction
{
    /// <summary>
    /// Builds a single-phase (positive-sequence) equivalent on <paramref name="baseMva"/>: three-phase lines use Z1 from the sequence matrix,
    /// one- and two-phase laterals use self minus mutual impedance; transformers keep their ratio (and 30° shift for Δ-Y); regulators
    /// become fixed-tap transformers with the average tap; loads are summed per bus (ZIP model from the load model); capacitors become bus
    /// shunts. Bus ids are 1..N in bus order and the source bus is the slack.
    /// </summary>
    public static PowerNetwork ToBalanced(this DistributionNetwork net, double baseMva = 10)
    {
        if (net.Source is null) throw new InvalidOperationException("Network has no source.");
        var pn = new PowerNetwork { Name = net.Name + " (positive sequence)", BaseMva = baseMva, FrequencyHz = net.FrequencyHz };
        var id = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < net.Buses.Count; i++)
        {
            var b = net.Buses[i];
            id[b.Name] = i + 1;
            bool slack = string.Equals(b.Name, net.Source.Bus, StringComparison.OrdinalIgnoreCase);
            pn.Buses.Add(new Bus
            {
                Id = i + 1, Name = b.Name, BaseKv = b.BaseKvLL, Type = slack ? BusType.Slack : BusType.PQ,
                Vm = slack ? net.Source.VoltagePu : 1, VaDeg = slack ? net.Source.AngleDeg : 0,
            });
        }
        var src = pn.Buses[id[net.Source.Bus] - 1];
        pn.Generators.Add(new Generator { Bus = src.Id, Name = "source", Vset = net.Source.VoltagePu, Kind = GeneratorKind.Grid, MBase = baseMva });

        foreach (var l in net.Lines.Where(l => l.Closed))
        {
            var z = CMat.Scale(l.Code.ZOhmPerKm, Math.Max(l.LengthKm, 1e-9));
            var y = CMat.Scale(l.Code.YSiemensPerKm, l.LengthKm);
            Complex z1 = Positive(z), y1 = Positive(y);
            double zb = net.Bus(l.From).BaseKvLL * net.Bus(l.From).BaseKvLL / baseMva;
            pn.Branches.Add(new Branch
            {
                From = id[l.From], To = id[l.To], Name = l.Name, Kind = CoreBranchKind.Line, R = z1.Real / zb, X = z1.Imaginary / zb,
                B = y1.Imaginary * zb, LengthKm = l.LengthKm, RateA = l.Code.NormalAmps > 0 ? Math.Sqrt(3) * net.Bus(l.From).BaseKvLL * l.Code.NormalAmps / 1000 : 0,
            });
        }
        foreach (var t in net.Transformers.Where(t => t.InService))
        {
            int np = t.Phases.Count();
            double kvaEq = t.KvaRating * 3 / np;
            var zpu = new Complex(t.RPercent, t.XPercent) / 100 * (baseMva * 1000 / kvaEq);
            double kvp = np == 3 ? t.KvPrimary : t.KvPrimary * Math.Sqrt(3);
            double kvs = np == 3 ? t.KvSecondary : t.KvSecondary * Math.Sqrt(3);
            double tap = kvp * t.TapPrimary / net.Bus(t.From).BaseKvLL / (kvs * t.TapSecondary / net.Bus(t.To).BaseKvLL);
            bool shift = t.Connection is TransformerConnection.DeltaWyeG or TransformerConnection.WyeDelta or TransformerConnection.WyeGDelta;
            pn.Branches.Add(new Branch
            {
                From = id[t.From], To = id[t.To], Name = t.Name, Kind = CoreBranchKind.Transformer, R = zpu.Real, X = zpu.Imaginary, Tap = tap,
                ShiftDeg = shift ? 30 : 0, RateA = t.KvaRating / 1000,
                Connection = t.Connection switch
                {
                    TransformerConnection.DeltaWyeG => WindingConnection.Dyn,
                    TransformerConnection.WyeDelta => WindingConnection.Yd,
                    TransformerConnection.WyeGDelta => WindingConnection.YNd,
                    TransformerConnection.DeltaDelta => WindingConnection.Dd,
                    _ => WindingConnection.YNyn,
                },
            });
        }
        foreach (var r in net.Regulators)
        {
            var ph = r.Phases.Indices();
            double ratio = ph.Average(p => r.Ratio(p));
            var zpu = new Complex(r.RPercent, r.XPercent) / 100 * (baseMva * 1000 / (r.KvaPerPhase * 3));
            pn.Branches.Add(new Branch
            {
                From = id[r.From], To = id[r.To], Name = r.Name, Kind = CoreBranchKind.Transformer, R = zpu.Real, X = zpu.Imaginary, Tap = 1 / ratio,
            });
        }
        foreach (var l in net.Loads.Where(l => l.InService))
        {
            var s = l.TotalKva / 1000;
            pn.Loads.Add(new Load
            {
                Bus = id[l.Bus], Name = l.Name, P = s.Real, Q = s.Imaginary,
                Model = l.Model switch
                {
                    LoadModel.ConstantImpedance => ZipModel.ConstantImpedance,
                    LoadModel.ConstantCurrent => ZipModel.ConstantCurrent,
                    _ => ZipModel.ConstantPower,
                },
            });
        }
        foreach (var c in net.Capacitors.Where(c => c.IsOn))
        {
            var bus = pn.Buses[id[c.Bus] - 1];
            double baseElemKv = c.Connection == LoadConnection.Wye ? bus.BaseKv / Math.Sqrt(3) : bus.BaseKv;
            double rated = double.IsNaN(c.RatedKv) ? baseElemKv : c.RatedKv;
            bus.Bs += c.TotalKvar / 1000 * (baseElemKv / rated) * (baseElemKv / rated);
        }
        pn.InvalidateIndex();
        return pn;
    }

    private static Complex Positive(Complex[,] m)
    {
        int n = m.GetLength(0);
        if (n == 3) return SequenceImpedance.FromPhaseMatrix(m).Positive;
        if (n == 1) return m[0, 0];
        Complex self = 0, mutual = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i == j) self += m[i, j]; else mutual += m[i, j];
        return self / n - mutual / (n * (n - 1));
    }
}

/// <summary>Distribution analysis services for dependency injection.</summary>
public interface IDistributionService
{
    DistributionPowerFlowResult Solve(DistributionNetwork network, DistributionPowerFlowOptions? options = null);
    DistributionTimeSeriesResult RunTimeSeries(DistributionNetwork network, IReadOnlyList<double> loadMultipliers, double stepSeconds = 3600);
    DistributionNetwork ReadOpenDss(string script, string? baseDirectory = null);
}

internal sealed class DistributionService : IDistributionService
{
    public DistributionPowerFlowResult Solve(DistributionNetwork network, DistributionPowerFlowOptions? options = null) =>
        UnbalancedPowerFlow.Solve(network, options);

    public DistributionTimeSeriesResult RunTimeSeries(DistributionNetwork network, IReadOnlyList<double> loadMultipliers, double stepSeconds = 3600) =>
        DistributionTimeSeries.Run(network, loadMultipliers, stepSeconds);

    public DistributionNetwork ReadOpenDss(string script, string? baseDirectory = null) => OpenDssReader.Parse(script, baseDirectory);
}

public static class DistributionBuilderExtensions
{
    /// <summary>Registers <see cref="IDistributionService"/>.</summary>
    public static ElectricalBuilder AddDistribution(this ElectricalBuilder b)
    {
        if (b.TryAddModule("Distribution")) b.Services.TryAddSingleton<IDistributionService, DistributionService>();
        return b;
    }
}
