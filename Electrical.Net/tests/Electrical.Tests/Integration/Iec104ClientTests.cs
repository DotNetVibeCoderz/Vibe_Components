using System.Net;
using System.Net.Sockets;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.SCADA.Iec104;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

/// <summary>IEC 60870-5-104 controlling/controlled stations over loopback: APCI state machine, k/w windows, timers, GI, commands.</summary>
public class Iec104ClientTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Fact]
    public void Codec_InterrogationClockAndExtendedTypes()
    {
        // The canonical general interrogation APDU: 68 0E | N(S)=0 N(R)=0 | C_IC_NA_1, VSQ 1, COT 6, OA 0, CA 1 | IOA 0 | QOI 20
        var gi = new Asdu(TypeId.C_IC_NA_1, CauseOfTransmission.Activation, 1, [new InformationObject(0, 20)]);
        Assert.Equal("680E0000000064010600010000000014", Hex(Iec104Codec.Encode(Apdu.I(0, 0, gi))));

        var t = new DateTimeOffset(2024, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);
        var cs = Iec104Codec.DecodeAsdu(Iec104Codec.EncodeAsdu(new Asdu(TypeId.C_CS_NA_1, CauseOfTransmission.Activation, 1, [new InformationObject(0, 0, Time: t)])));
        Assert.Equal(t, cs.Objects[0].Time);
        Assert.Equal(6 + 3 + 7, Iec104Codec.EncodeAsdu(cs).Length);

        var mixed = new (TypeId Type, double Value, int Size)[]
        {
            (TypeId.M_DP_NA_1, 2, 1), (TypeId.M_ME_NA_1, -0.5, 3), (TypeId.M_ME_NB_1, -1234, 3), (TypeId.C_DC_NA_1, 1, 1),
            (TypeId.C_SE_NC_1, 49.875, 5), (TypeId.M_EI_NA_1, 0, 1), (TypeId.M_DP_TB_1, 1, 8), (TypeId.M_ME_TE_1, 77, 10),
        };
        foreach (var (type, v, size) in mixed)
        {
            var a = new Asdu(type, CauseOfTransmission.Spontaneous, 7, [new InformationObject(4001, v, Time: Asdu.HasTime(type) ? t : null, Select: type == TypeId.C_SE_NC_1, Qualifier: 3)]);
            var bytes = Iec104Codec.EncodeAsdu(a);
            Assert.Equal(6 + 3 + size, bytes.Length);
            var o = Iec104Codec.DecodeAsdu(bytes).Objects[0];
            Assert.Equal(v, o.Value, 6);
            if (Asdu.HasTime(type)) Assert.Equal(t, o.Time);
        }
        // normalized −0.5 → NVA 0xC000; double command OFF with QU = 3, S/E = 0 → DCO 0x0D
        Assert.Equal("00C000", Hex(Iec104Codec.EncodeAsdu(new Asdu(TypeId.M_ME_NA_1, CauseOfTransmission.Spontaneous, 1, [new InformationObject(1, -0.5)]))[9..]));
        Assert.Equal(0x0D, Iec104Codec.EncodeAsdu(new Asdu(TypeId.C_DC_NA_1, CauseOfTransmission.Activation, 1, [new InformationObject(1, 1, Qualifier: 3)]))[9]);
    }

    private static (TagDatabase Tags, Iec104PointMap Map) Station()
    {
        var tags = new TagDatabase();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tags.Add("CB1.POS"); tags.UpdateValue("CB1.POS", 1, t0);
        tags.Add("DS1.POS"); tags.UpdateValue("DS1.POS", 0, t0);
        tags.Add("LINE1.P", "MW"); tags.UpdateValue("LINE1.P", 123.25, t0);
        tags.Add("LINE1.I", "pu"); tags.UpdateValue("LINE1.I", 0.75, t0);
        tags.Add("TAP", ""); tags.UpdateValue("TAP", -3, t0);
        tags.Add("CB1.CMD"); tags.UpdateValue("CB1.CMD", 0, t0);
        tags.Add("DS1.CMD"); tags.UpdateValue("DS1.CMD", 0, t0);
        tags.Add("GEN1.PSET", "MW"); tags.UpdateValue("GEN1.PSET", 0, t0);
        var map = new Iec104PointMap();
        map.Add(1, "CB1.POS", TypeId.M_SP_NA_1, group: 1);
        map.Add(2, "DS1.POS", TypeId.M_DP_NA_1, group: 1);
        map.Add(100, "LINE1.P", TypeId.M_ME_NC_1, group: 2);
        map.Add(101, "LINE1.I", TypeId.M_ME_NA_1, fullScale: 2, group: 2);
        map.Add(102, "TAP", TypeId.M_ME_NB_1, group: 2);
        map.Add(5001, "CB1.CMD", TypeId.C_SC_NA_1, selectBeforeOperate: true);
        map.Add(5002, "DS1.CMD", TypeId.C_DC_NA_1);
        map.Add(6001, "GEN1.PSET", TypeId.C_SE_NC_1);
        map.Add(103, "GEN1.PSET", TypeId.M_ME_NC_1, group: 2); // set-point feedback
        return (tags, map);
    }

    private static async Task<(Iec104Server Server, Iec104Client Client)> Start(TagDatabase tags, Iec104PointMap map, Iec104Parameters? p = null)
    {
        var server = new Iec104Server(tags, map, new Iec104ServerOptions { Parameters = p ?? new() }, IPAddress.Loopback, 0);
        server.Start();
        var client = new Iec104Client(p) { RequestTimeout = Generous };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    private static async Task Until(Func<bool> cond, CancellationToken ct)
    {
        while (!cond()) await Task.Delay(10, ct);
    }

    [Fact]
    public async Task GeneralInterrogation_ClockSync_AndCommands()
    {
        var (tags, map) = Station();
        var (server, client) = await Start(tags, map);
        await using var _ = server;
        await using var __ = client;
        using var cts = new CancellationTokenSource(Generous);

        await client.StartDataTransferAsync(cts.Token);
        Assert.True(client.Connection.DataTransferActive);
        await Until(() => client.EndOfInitializationCount == 1, cts.Token);

        var gi = await client.GeneralInterrogationAsync(cts.Token);
        Assert.Equal(6, gi.Count);
        var byIoa = gi.ToDictionary(x => x.Object.Address);
        Assert.Equal((TypeId.M_SP_NA_1, 1.0), (byIoa[1].Type, byIoa[1].Object.Value));
        Assert.Equal((TypeId.M_DP_NA_1, 1.0), (byIoa[2].Type, byIoa[2].Object.Value));   // DPI 1 = off
        Assert.Equal(123.25, byIoa[100].Object.Value, 4);
        Assert.Equal(0.375, byIoa[101].Object.Value, 4);                                  // 0.75 / full scale 2
        Assert.Equal(-3, byIoa[102].Object.Value);
        Assert.Equal(2, (await client.InterrogateAsync(21, cts.Token)).Count);             // group 1

        var target = new DateTimeOffset(2031, 7, 8, 9, 10, 11, 120, TimeSpan.Zero);
        var echoed = await client.ClockSyncAsync(target, cts.Token);
        Assert.InRange((echoed!.Value - target).TotalSeconds, -1, 5);
        Assert.InRange((server.Now - target).TotalSeconds, -1, 5);

        // select-before-operate point: execute without select is rejected, select + execute works
        var fail = await client.SingleCommandAsync(5001, true, selectBeforeOperate: false, ct: cts.Token);
        Assert.False(fail.Success);
        Assert.Equal(0, tags["CB1.CMD"].Value);
        var ok = await client.SingleCommandAsync(5001, true, selectBeforeOperate: true, ct: cts.Token);
        Assert.True(ok.Success && ok.Terminated);
        Assert.Equal(1, tags["CB1.CMD"].Value);

        var dc = await client.DoubleCommandAsync(5002, on: true, ct: cts.Token);
        Assert.True(dc.Success);
        Assert.Equal(1, tags["DS1.CMD"].Value);

        var sp = await client.SetpointAsync(6001, 87.5f, ct: cts.Token);
        Assert.True(sp.Success);
        Assert.Equal(87.5, tags["GEN1.PSET"].Value);
        await Until(() => client.Values.TryGetValue(103, out var v) && v.Object.Value == 87.5, cts.Token); // spontaneous feedback
        Assert.Equal(CauseOfTransmission.Spontaneous, client.Values[103].Cause);
        Assert.Equal(TypeId.M_ME_TF_1, client.Values[103].Type);

        var unknown = await client.SingleCommandAsync(9999, true, ct: cts.Token);
        Assert.Equal((false, CauseOfTransmission.UnknownObjectAddress), (unknown.Success, unknown.Cause));
    }

    [Fact]
    public async Task Spontaneous_WindowAcknowledgement_StopDt_AndTagBridge()
    {
        var (tags, map) = Station();
        var p = new Iec104Parameters { K = 12, W = 8 };
        var (server, client) = await Start(tags, map, p);
        await using var _ = server;
        await using var __ = client;
        using var cts = new CancellationTokenSource(Generous);
        await client.StartDataTransferAsync(cts.Token);

        int received = 0;
        client.AsduReceived += a => { if (a.Cause == CauseOfTransmission.Spontaneous) Interlocked.Increment(ref received); };
        for (int i = 1; i <= 40; i++) tags.UpdateValue("LINE1.P", 100 + i);
        await Until(() => Volatile.Read(ref received) == 40, cts.Token);
        Assert.True(client.Connection.SFramesSent >= 40 / p.W, $"S-frames {client.Connection.SFramesSent}"); // ack at least every w
        Assert.Equal(140, client.Values[100].Object.Value, 4);

        var mirror = new TagDatabase();
        foreach (var t in tags.All) mirror.Add(t.Name, t.Unit);
        await client.GeneralInterrogationAsync(cts.Token);
        Assert.True(client.ApplyTo(mirror, map) >= 6);
        Assert.Equal(140, mirror["LINE1.P"].Value, 4);
        Assert.Equal(0.75, mirror["LINE1.I"].Value, 4);                                   // normalized × full scale

        await client.StopDataTransferAsync(cts.Token);
        int before = Volatile.Read(ref received);
        tags.UpdateValue("LINE1.P", 1);
        await client.TestAsync(cts.Token);                                                 // round trip after the change
        Assert.Equal(before, Volatile.Read(ref received));                                 // no spontaneous data while stopped
    }

    [Fact]
    public async Task Timers_TestFrameOnIdleAndT2Acknowledge()
    {
        var (tags, map) = Station();
        var p = new Iec104Parameters { T1 = TimeSpan.FromSeconds(5), T2 = TimeSpan.FromMilliseconds(100), T3 = TimeSpan.FromMilliseconds(300) };
        var (server, client) = await Start(tags, map, p);
        await using var _ = server;
        await using var __ = client;
        using var cts = new CancellationTokenSource(Generous);
        await client.StartDataTransferAsync(cts.Token);
        await Until(() => client.EndOfInitializationCount == 1, cts.Token);
        // one I-frame (M_EI_NA_1) received, fewer than w → acknowledged by an S-frame after t2
        await Until(() => client.Connection.SFramesSent >= 1, cts.Token);
        await Until(() => server is { } && client.Connection.TestFramesSent >= 1, cts.Token); // t3 idle → TESTFR act
        await Until(() => client.Connection.TestFramesReceived >= 1, cts.Token);               // server's t3 too
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task KWindow_BlocksUntilAcknowledged_AndSequenceErrorCloses()
    {
        var (tags, map) = Station();
        var server = new Iec104Server(tags, map, new Iec104ServerOptions { Parameters = new() { K = 3 }, SendEndOfInitialization = false }, IPAddress.Loopback, 0);
        await using var _ = server;
        server.Start();
        using var cts = new CancellationTokenSource(Generous);
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, server.Port, cts.Token);
        var s = raw.GetStream();

        async Task<Apdu> Read()
        {
            var head = new byte[2];
            await s.ReadExactlyAsync(head, cts.Token);
            var f = new byte[2 + head[1]];
            head.CopyTo(f, 0);
            await s.ReadExactlyAsync(f.AsMemory(2), cts.Token);
            return Iec104Codec.Decode(f);
        }

        await s.WriteAsync(Iec104Codec.Encode(Apdu.U(UFunction.StartDtAct)), cts.Token);
        Assert.Equal(UFunction.StartDtCon, (await Read()).Function);
        var gi = new Asdu(TypeId.C_IC_NA_1, CauseOfTransmission.Activation, 1, [new InformationObject(0, 20)]);
        await s.WriteAsync(Iec104Codec.Encode(Apdu.I(0, 0, gi)), cts.Token);
        // GI response = ActCon + 4 data ASDUs (SP, DP, NC, NA, NB → 5 types) + ActTerm = 7 I-frames; k = 3 → only 3 before an ack
        var first = new List<Apdu>();
        for (int i = 0; i < 3; i++) first.Add(await Read());
        Assert.Equal([0, 1, 2], first.Select(a => (int)a.SendSequence));
        await Task.Delay(300, cts.Token);
        Assert.Equal(0, raw.Available);                                                   // window full: nothing more until acknowledged
        var frames = first;
        await s.WriteAsync(Iec104Codec.Encode(Apdu.S(3)), cts.Token);                   // acknowledge 0..2 → window opens
        while (frames[^1].Asdu?.Cause != CauseOfTransmission.ActivationTermination)
        {
            frames.Add(await Read());
            if (frames.Count % 3 == 0) await s.WriteAsync(Iec104Codec.Encode(Apdu.S((ushort)frames.Count)), cts.Token);
        }
        Assert.Equal(CauseOfTransmission.ActivationConfirmation, frames[0].Asdu!.Cause);
        Assert.Equal(Enumerable.Range(0, frames.Count), frames.Select(a => (int)a.SendSequence));

        // sequence error: N(S) = 5 while 1 is expected → the controlled station closes the connection
        await s.WriteAsync(Iec104Codec.Encode(Apdu.I(5, (ushort)frames.Count, gi)), cts.Token);
        var buf = new byte[256];
        int n;
        do n = await s.ReadAsync(buf, cts.Token); while (n > 0);
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task RtuSimulator_ServesPowerFlowOverIec104()
    {
        var pf = PowerFlowSolver.Solve(TestCases.Case14());
        var rtu = new RtuSimulator(pf);
        var tags = new TagDatabase();
        rtu.UpdateTags(tags);
        var map = rtu.CreateIec104Map();
        var (server, client) = await Start(tags, map);
        await using var _ = server;
        await using var __ = client;
        using var cts = new CancellationTokenSource(Generous);
        await client.StartDataTransferAsync(cts.Token);
        var gi = await client.GeneralInterrogationAsync(cts.Token);
        Assert.Equal(rtu.Points.Count, gi.Count);
        int analogIndex = rtu.Points.Where(p => p.DataType != TagDataType.Bool).Select(p => p.Name).ToList().IndexOf("BUS4.VM");
        Assert.Equal(rtu.MeasuredValue(rtu.Points.Single(p => p.Name == "BUS4.VM")), client.Values[1000 + (uint)analogIndex].Object.Value, 5);
        Assert.Equal(1, client.Values[1].Object.Value); // BR0.CLOSED
    }

    [Fact]
    public void AddIec104_RegistersMapAndOptions()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddIec104(new Iec104ServerOptions { CommonAddress = 7 })).BuildServiceProvider();
        Assert.Contains("IEC104", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
        Assert.NotNull(sp.GetRequiredService<TagDatabase>());
        Assert.NotNull(sp.GetRequiredService<Iec104PointMap>());
        Assert.Equal((ushort)7, sp.GetRequiredService<Iec104ServerOptions>().CommonAddress);
    }
}
