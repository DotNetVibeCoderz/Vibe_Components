using System.Net;
using System.Numerics;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Dynamics;
using Electrical.Pmu;
using Electrical.Pmu.C37118;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

/// <summary>PMU simulator, TCP/UDP transport, PDC alignment and linear PMU state estimation.</summary>
public class PmuStreamingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<PowerFlowResult> Case14 = new(() => PowerFlowSolver.Solve(TestCases.Case14()));

    [Fact]
    public async Task Tcp_PdcRequestsConfigHeaderAndStreamsData()
    {
        var pf = Case14.Value;
        var sim = new PmuSimulator(pf, [1, 2, 6, 9], new PmuSimulatorOptions { StreamIdCode = 14, DataRate = 50, FrequencyOffsetHz = 0.1 });
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await using var server = new PmuServer(sim, IPAddress.Loopback, 0, new PmuServerOptions { Paced = false, SimulatedStart = start });
        server.Start();
        using var cts = new CancellationTokenSource(Generous);
        await using var pdc = new PdcClient(14);
        await pdc.ConnectAsync("127.0.0.1", server.Port, cts.Token);

        var cfg = await pdc.RequestConfigurationAsync(ct: cts.Token);
        Assert.Equal(4, cfg.Pmus.Count);
        Assert.Equal(pf.Network.FrequencyHz, cfg.Pmus[0].NominalFrequencyHz);
        Assert.Contains("PMU simulator", await pdc.RequestHeaderAsync(cts.Token));
        var cfg1 = await pdc.RequestConfigurationAsync(C37FrameType.Config1, cts.Token);
        Assert.Equal(C37FrameType.Config1, cfg1.Type);
        var cfg3 = await pdc.RequestConfigurationAsync(C37FrameType.Config3, cts.Token);
        Assert.Equal(C37FrameType.Config3, cfg3.Type);

        await pdc.StartDataAsync(cts.Token);
        var frames = new List<C37DataFrame>();
        while (frames.Count < 10) frames.Add(await pdc.ReadDataFrameAsync(cts.Token));
        await pdc.StopDataAsync(cts.Token);

        for (int k = 0; k < frames.Count; k++)
            Assert.Equal(start.AddMilliseconds(20 * k), frames[k].Time.ToDateTime(cfg.TimeBase));
        // Bus 1 voltage phasor: Vm·V_LN base at the power-flow angle, rotated by 2π·0.1 Hz·t.
        var bus1 = pf.Bus(1);
        double vbase = TestCases.Case14().GetBus(1).BaseKv * 1000 / Math.Sqrt(3);
        var v = frames[5].Blocks[0].Phasors[0];
        Assert.Equal(bus1.Vm * vbase, v.Magnitude, 1e-3 * vbase);
        double expectedDeg = bus1.VaDeg + 360 * 0.1 * ((frames[5].Time.ToDateTime() - sim.Options.ReferenceTime).TotalSeconds);
        Assert.Equal(0, Math.IEEERemainder(v.Phase * 180 / Math.PI - expectedDeg, 360), 3);
        Assert.Equal(sim.Configuration.Pmus[0].NominalFrequencyHz + 0.1, frames[0].Blocks[0].FrequencyHz, 4);

        // After data off, commands still work on the same connection (stale data frames are skipped).
        var again = await pdc.RequestConfigurationAsync(ct: cts.Token);
        Assert.Equal(cfg.Pmus.Count, again.Pmus.Count);
        Assert.Equal(0, pdc.InvalidFrames);
    }

    [Fact]
    public async Task Udp_PublishesValidFrames()
    {
        var sim = new PmuSimulator(Case14.Value, [4]);
        using var rx = new PdcUdpReceiver(0);
        using var tx = new PmuUdpPublisher(new IPEndPoint(IPAddress.Loopback, rx.Port));
        using var cts = new CancellationTokenSource(Generous);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero);
        await tx.SendAsync(C37Codec.Encode(sim.CreateFrame(t), sim.Configuration), cts.Token);
        var raw = await rx.ReceiveFrameAsync(cts.Token);
        var f = C37Codec.DecodeData(raw, sim.Configuration);
        Assert.Equal(t, f.Time.ToDateTime());
        Assert.Equal(sim.Bindings[0].Count, f.Blocks[0].Phasors.Count);
    }

    [Fact]
    public void LinearSE_Case14_ExactAndNoisy()
    {
        var pf = Case14.Value;
        int[] pmus = [2, 6, 7, 9]; // a known minimal observing placement for IEEE 14 (with branch currents)
        var exact = LinearStateEstimator.FromPowerFlow(pf, pmus, noise: 0);
        Assert.True(LinearStateEstimator.IsObservable(pf.Network, exact));
        var se = LinearStateEstimator.Estimate(pf.Network, exact);
        for (int i = 0; i < pf.Voltages.Length; i++) Assert.True((se.Voltages[i] - pf.Voltages[i]).Magnitude < 1e-9);
        Assert.True(se.Objective < 1e-12);

        var noisy = LinearStateEstimator.FromPowerFlow(pf, pmus, sigmaVoltagePu: 0.002, sigmaCurrentPu: 0.004, seed: 11);
        var se2 = LinearStateEstimator.Estimate(pf.Network, noisy);
        double maxVm = pf.Buses.Max(b => Math.Abs(se2.Vm(b.BusId) - b.Vm));
        double maxVa = pf.Buses.Max(b => Math.Abs(se2.VaDeg(b.BusId) - b.VaDeg));
        Assert.True(maxVm < 0.01, $"max |ΔVm| {maxVm}");
        Assert.True(maxVa < 0.5, $"max |ΔVa| {maxVa}°");
        // J follows χ² with 2(m − n) degrees of freedom: well below the 99 % quantile ≈ dof + 3√(2·dof).
        Assert.True(se2.Objective < se2.DegreesOfFreedom + 3 * Math.Sqrt(2.0 * se2.DegreesOfFreedom), $"J = {se2.Objective}, dof {se2.DegreesOfFreedom}");

        Assert.False(LinearStateEstimator.IsObservable(pf.Network, LinearStateEstimator.FromPowerFlow(pf, [1], noise: 0)));
        Assert.Throws<InvalidOperationException>(() => LinearStateEstimator.Estimate(pf.Network, LinearStateEstimator.FromPowerFlow(pf, [1], noise: 0)));
    }

    [Fact]
    public void LinearSE_FromSimulatedC37Frame()
    {
        var pf = Case14.Value;
        var sim = new PmuSimulator(pf, [2, 6, 7, 9], new PmuSimulatorOptions { Format = C37DataFormat.FloatRectangular });
        var frame = C37Codec.DecodeData(C37Codec.Encode(sim.CreateFrame(sim.Options.ReferenceTime), sim.Configuration), sim.Configuration);
        var z = LinearStateEstimator.FromFrame(pf.Network, frame, sim.Bindings);
        var se = LinearStateEstimator.Estimate(pf.Network, z);
        var v = se.Referenced(1, pf.Bus(1).VaDeg);
        for (int i = 0; i < v.Length; i++) Assert.True((v[i] - pf.Voltages[i]).Magnitude < 1e-5, $"bus {i}: {(v[i] - pf.Voltages[i]).Magnitude}"); // float32 round-off
    }

    private static C37DataFrame Frame(ushort id, int k) =>
        new(id, new C37Time(1000, (uint)(k * 20_000)), [new PmuDataBlock(PmuStatus.None, [Complex.One], 50, 0)]);

    [Fact]
    public void Pdc_AlignsByTimestampWithWaitTime()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var pdc = new PdcAlignmentBuffer([1, 2, 3], TimeSpan.FromMilliseconds(50));
        Assert.Empty(pdc.Add(Frame(1, 0), t0));
        Assert.Empty(pdc.Add(Frame(2, 0), t0.AddMilliseconds(5)));
        var r = pdc.Add(Frame(3, 0), t0.AddMilliseconds(8));          // complete → released immediately
        Assert.True(Assert.Single(r).IsComplete);

        Assert.Empty(pdc.Add(Frame(2, 1), t0.AddMilliseconds(20)));   // stream 1 never sends k = 1
        Assert.Empty(pdc.Add(Frame(3, 1), t0.AddMilliseconds(22)));
        Assert.Empty(pdc.Add(Frame(1, 2), t0.AddMilliseconds(40)));
        Assert.Empty(pdc.Add(Frame(2, 2), t0.AddMilliseconds(41)));
        Assert.Empty(pdc.Add(Frame(3, 2), t0.AddMilliseconds(42)));   // k = 2 complete but waits behind k = 1 (order kept)
        Assert.Equal(2, pdc.PendingCount);
        var released = pdc.Flush(t0.AddMilliseconds(70));             // k = 1 expired (first arrival 20 ms + 50 ms)
        Assert.Equal(2, released.Count);
        Assert.Equal(new ushort[] { 1 }, released[0].Missing);
        Assert.True(released[1].IsComplete);
        Assert.True(released[0].Time.Key < released[1].Time.Key);

        Assert.Empty(pdc.Add(Frame(1, 1), t0.AddMilliseconds(75)));   // late frame dropped
        Assert.Equal(1, pdc.LateFrames);
        Assert.Empty(pdc.Add(Frame(9, 3), t0.AddMilliseconds(80)));
        Assert.Equal(1, pdc.UnknownFrames);
        Assert.Empty(pdc.Add(Frame(1, 4), t0.AddMilliseconds(90)));
        Assert.Equal(new ushort[] { 2, 3 }, Assert.Single(pdc.FlushAll()).Missing);
    }

    [Fact]
    public void TimeSeriesSource_ReplaysDynamicsResult()
    {
        var sys = DynamicTestCases.KundurTwoArea().Build();
        var res = DynamicSimulation.Run(sys, [DynamicEvent.LoadStep(0.2, 7, 100)], new SimulationOptions { EndTimeS = 2 });
        var net = sys.Network;
        var src = new PmuTimeSeriesSource(net, res.Time, res.BusVoltagePu, res.BusFrequencyHz, null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), dataRate: 25);
        Assert.Equal(net.BusCount, src.Configuration.Pmus.Count);
        var cfg = src.Configuration;
        var late = C37Codec.DecodeData(C37Codec.Encode(src.CreateFrame(src.StartTime.AddSeconds(1.8)), cfg), cfg);
        int i7 = net.IndexOf(7);
        double f = late.Blocks[i7].FrequencyHz;
        Assert.True(f < net.FrequencyHz, $"frequency should sag after a 100 MW load step, got {f}");
        Assert.Equal(res.At(res.BusVoltagePu[i7], 1.8) * net.Buses[i7].BaseKv * 1000 / Math.Sqrt(3), late.Blocks[i7].Phasors[0].Magnitude, 1);
    }

    [Fact]
    public void AddPmu_RegistersEstimatorAndPdc()
    {
        var sp = new ServiceCollection()
            .AddElectrical(b => b.AddPmu(new PmuOptions { PdcStreams = [1, 2] }).AddPmu())
            .BuildServiceProvider();
        Assert.Contains("PMU", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
        Assert.NotNull(sp.GetRequiredService<PhasorEstimator>());
        Assert.Equal(2, sp.GetRequiredService<PdcAlignmentBuffer>().ExpectedStreams.Count);
    }
}
