using System.Net;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.SCADA.Dnp3;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

/// <summary>
/// DNP3 (IEEE 1815-2012): CRC-16/DNP check value 0xEA82 (CRC catalogue) and the link-layer frame
/// <c>05 64 05 C0 01 00 00 04 E9 21</c> (RESET_LINK_STATES from master 1024 to outstation 1, the classic IEEE 1815 / opendnp3 example);
/// transport segmentation; object headers; an outstation/master pair over loopback TCP.
/// </summary>
public class Dnp3Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Fact]
    public void Crc_AndLinkFrame_MatchKnownVectors()
    {
        Assert.Equal(0xEA82, Dnp3Crc.Compute("123456789"u8));
        var reset = new Dnp3LinkFrame(true, true, (byte)LinkFunction.ResetLinkStates, 1, 1024, []);
        Assert.Equal("056405C001000004E921", Hex(reset.Encode()));
        var back = Dnp3LinkFrame.Decode(Convert.FromHexString("056405C001000004E921"));
        Assert.Equal((true, true, (byte)0, (ushort)1, (ushort)1024), (back.FromMaster, back.IsPrimary, back.Function, back.Destination, back.Source));

        // 40 octets of user data → blocks of 16 + 16 + 8, each with its own CRC: 10 + 40 + 3·2 = 56 octets
        var data = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var f = Dnp3LinkFrame.UnconfirmedData(false, 1, 1024, data).Encode();
        Assert.Equal(56, f.Length);
        Assert.Equal(0x2D, f[2]);  // LEN = 5 + 40
        Assert.Equal(0x44, f[3]);  // DIR = 0, PRM = 1, UNCONFIRMED_USER_DATA
        Assert.Equal(data, Dnp3LinkFrame.Decode(f).UserData);
        f[20] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => Dnp3LinkFrame.Decode(f));
    }

    [Fact]
    public void Transport_And_ObjectHeaders()
    {
        var frag = Enumerable.Range(0, 600).Select(i => (byte)(i * 7)).ToArray();
        int seq = 62;
        var segs = Dnp3Transport.Segment(frag, ref seq);
        Assert.Equal(3, segs.Count);
        Assert.Equal(0x40 | 62, segs[0][0]);       // FIR, seq 62
        Assert.Equal(63, segs[1][0]);              // middle
        Assert.Equal(0x80 | 0, segs[2][0]);        // FIN, seq wraps to 0
        var rx = new Dnp3TransportReassembler();
        Assert.Null(rx.Add(segs[0]));
        Assert.Null(rx.Add(segs[1]));
        Assert.Equal(frag, rx.Add(segs[2]));
        Assert.Null(rx.Add(segs[1]));              // out of sequence without FIR is discarded

        // Integrity poll request (seq 3): C3 01 3C02 06 3C03 06 3C04 06 3C01 06
        var poll = Dnp3Fragment.Request(3, Dnp3Function.Read, new Dnp3ObjectWriter().All(60, 2).All(60, 3).All(60, 4).All(60, 1).ToArray());
        Assert.Equal("C3013C02063C03063C04063C0106", Hex(poll.Encode()));

        // CROB latch on, count 1, on 100 ms, off 0, with 0x17 prefix index 5 — g12v1 q17 n=1 idx 5
        var crob = new Dnp3ObjectWriter().Prefixed(12, 1, [(5u, new Crob(Dnp3OpType.LatchOn, OnTimeMs: 100).Encode())]).ToArray();
        Assert.Equal("0C0117010503016400000000000000" + "00", Hex(crob));

        // All supported qualifiers parse
        var w = new Dnp3ObjectWriter()
            .Range(1, 2, 3, [[0x81], [0x01]])                                              // 0x00
            .Range(30, 5, 300, [Dnp3Objects.Analog(30, 5, 1.5, Dnp3Flags.Online)])          // 0x01
            .Count(50, 1, [Dnp3Objects.Time(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123))]) // 0x07
            .Prefixed(32, 7, [(9u, Dnp3Objects.Analog(32, 7, -2.25, Dnp3Flags.Online, DateTimeOffset.FromUnixTimeMilliseconds(42)))], forceWide: true) // 0x28
            .PackedRange(80, 1, 7, [false]);
        var hs = Dnp3ObjectParser.Parse(w.ToArray(), withData: true);
        Assert.Equal([Dnp3Qualifier.Range8, Dnp3Qualifier.Range16, Dnp3Qualifier.Count8, Dnp3Qualifier.Prefix16Count16, Dnp3Qualifier.Range8], hs.Select(h => h.Qualifier));
        Assert.Equal([3u, 4u], hs[0].Items.Select(i => i.Index));
        Assert.Equal(0x81, hs[0].Items[0].Data[0]);
        Assert.Equal(1.5, Dnp3Objects.ReadAnalog(30, 5, hs[1].Items[0].Data).Value);
        Assert.Equal(1_700_000_000_123, Dnp3Objects.ReadTime(hs[2].Items[0].Data).ToUnixTimeMilliseconds());
        var ev = Dnp3Objects.ReadAnalog(32, 7, hs[3].Items[0].Data);
        Assert.Equal((9u, -2.25, 42L), (hs[3].Items[0].Index, ev.Value, ev.Time!.Value.ToUnixTimeMilliseconds()));
        Assert.Equal(7u, hs[4].Items[0].Index);
        var all = Dnp3ObjectParser.Parse([60, 1, 0x06, 1, 2, 0x17, 0], withData: false);
        Assert.Equal((Dnp3Qualifier.All, Dnp3Qualifier.Prefix8Count8), (all[0].Qualifier, all[1].Qualifier));
        Assert.Throws<NotSupportedException>(() => Dnp3ObjectParser.Parse([99, 9, 0x06], withData: false));
    }

    private static Dnp3Database SampleDatabase()
    {
        var db = new Dnp3Database(eventBufferSize: 50);
        for (ushort i = 0; i < 3; i++) db.AddBinaryInput(i, i == 1, Dnp3PointClass.Class1);
        for (ushort i = 0; i < 3; i++) db.AddAnalogInput(i, 10 * i + 0.5, Dnp3PointClass.Class2, deadband: 0.1);
        db.AddBinaryOutput(0);
        db.AddAnalogOutput(0, 50);
        return db;
    }

    private static async Task<(Dnp3Outstation Os, Dnp3Master Master)> Connect(Dnp3Database db, Dnp3OutstationOptions? o = null)
    {
        var os = new Dnp3Outstation(db, o, IPAddress.Loopback, 0);
        os.Start();
        var m = new Dnp3Master(1, os.Options.LocalAddress) { ResponseTimeout = Generous };
        await m.ConnectAsync("127.0.0.1", os.Port);
        return (os, m);
    }

    [Fact]
    public async Task IntegrityPoll_EventsAndConfirm()
    {
        var db = SampleDatabase();
        var (os, m) = await Connect(db);
        await using var _ = os;
        await using var __ = m;
        using var cts = new CancellationTokenSource(Generous);

        var r = await m.IntegrityPollAsync(cts.Token);
        Assert.True(r.Iin.HasFlag(Dnp3Iin.DeviceRestart));
        Assert.Equal(3, r.Measurements.BinaryInputs.Count);
        Assert.True(r.Measurements.BinaryInputs[1].State);
        Assert.Equal(20.5, r.Measurements.AnalogInputs[2].Value, 6);
        Assert.Equal(Dnp3Flags.Online, r.Measurements.AnalogInputs[2].Flags);
        Assert.Equal(50, r.Measurements.AnalogOutputs[0].Value, 6);
        Assert.False(r.Measurements.BinaryOutputs[0].State);
        Assert.Empty(r.Measurements.Events);

        await m.ClearRestartAsync(cts.Token);
        Assert.False(m.LastIin.HasFlag(Dnp3Iin.DeviceRestart));

        var t = new DateTimeOffset(2026, 5, 1, 12, 0, 0, 250, TimeSpan.Zero);
        db.UpdateAnalogInput(1, 10.55, time: t);                     // inside the 0.1 deadband → no event
        db.UpdateAnalogInput(1, 11.0, time: t);                      // event (class 2)
        db.UpdateBinaryInput(2, true, time: t.AddMilliseconds(5));   // event (class 1)
        Assert.Equal(1, db.EventCount(Dnp3PointClass.Class2));

        var e = await m.EventPollAsync(ct: cts.Token);
        Assert.Equal(2, e.Measurements.Events.Count);
        var ai = e.Measurements.Events.Single(x => x.Type == Dnp3PointType.AnalogInput);
        Assert.Equal((ushort)1, ai.Index);
        Assert.Equal(11.0, ai.Value, 6);
        Assert.Equal(t, ai.Time);                                    // g32v7 carries the event time
        Assert.Equal(t.AddMilliseconds(5), e.Measurements.Events.Single(x => x.Type == Dnp3PointType.BinaryInput).Time);
        Assert.True(e.Iin.HasFlag(Dnp3Iin.Class1Events) && e.Iin.HasFlag(Dnp3Iin.Class2Events)); // still buffered until confirmed

        var again = await m.EventPollAsync(ct: cts.Token);           // the master confirmed → buffers are empty
        Assert.Empty(again.Measurements.Events);
        Assert.False(again.Iin.HasFlag(Dnp3Iin.Class2Events));
        Assert.Equal(0, db.EventCount(Dnp3PointClass.Class1) + db.EventCount(Dnp3PointClass.Class2));
        Assert.Equal(11.0, m.Values.AnalogInputs[1].Value, 6);

        var unknown = await m.RequestAsync(Dnp3Function.Read, [99, 1, 0x06], cts.Token);
        Assert.True(unknown!.Iin.HasFlag(Dnp3Iin.ObjectUnknown));
        var nofc = await m.RequestAsync(Dnp3Function.ColdRestart, null, cts.Token);
        Assert.True(nofc!.Iin.HasFlag(Dnp3Iin.NoFunctionCodeSupport));
    }

    [Fact]
    public async Task Controls_SelectBeforeOperate_DirectOperate_AndRejections()
    {
        var db = SampleDatabase();
        var (os, m) = await Connect(db);
        await using var _ = os;
        await using var __ = m;
        using var cts = new CancellationTokenSource(Generous);
        var executed = new List<Dnp3CommandEvent>();
        os.Commanded += executed.Add;

        Assert.Equal(Dnp3CommandStatus.Success, await m.OperateBinaryAsync(0, Crob.LatchOn(), selectBeforeOperate: true, cts.Token));
        Assert.True(db.Get(Dnp3PointType.BinaryOutput, 0).State);
        Assert.Equal(Dnp3CommandStatus.Success, await m.OperateBinaryAsync(0, Crob.Trip(), selectBeforeOperate: false, cts.Token));
        Assert.False(db.Get(Dnp3PointType.BinaryOutput, 0).State);
        Assert.Equal(2, executed.Count);
        Assert.True(executed[0].Selected);

        // OPERATE without a preceding SELECT (or with different objects) → NO_SELECT, nothing executed
        var obj = new Dnp3ObjectWriter().Prefixed(12, 1, [(0u, Crob.LatchOn().Encode())]).ToArray();
        Assert.Equal(Dnp3CommandStatus.NoSelect, await m.SendControlAsync(Dnp3Function.Operate, obj, cts.Token));
        Assert.Equal(Dnp3CommandStatus.Success, await m.SendControlAsync(Dnp3Function.Select, obj, cts.Token));
        var other = new Dnp3ObjectWriter().Prefixed(12, 1, [(0u, Crob.LatchOff().Encode())]).ToArray();
        Assert.Equal(Dnp3CommandStatus.NoSelect, await m.SendControlAsync(Dnp3Function.Operate, other, cts.Token));
        Assert.Equal(2, executed.Count);

        Assert.Equal(Dnp3CommandStatus.NotSupported, await m.OperateBinaryAsync(7, Crob.LatchOn(), true, cts.Token));
        Assert.Equal(Dnp3CommandStatus.Success, await m.OperateAnalogAsync(0, 62.5, variation: 3, selectBeforeOperate: true, cts.Token));
        Assert.Equal(62.5, db.Get(Dnp3PointType.AnalogOutput, 0).Value, 6);
        Assert.Equal(Dnp3CommandStatus.Success, await m.OperateAnalogAsync(0, 40, variation: 1, selectBeforeOperate: false, cts.Token));
        Assert.Equal(40, db.Get(Dnp3PointType.AnalogOutput, 0).Value);
        await m.RequestAsync(Dnp3Function.DirectOperateNoAck, new Dnp3ObjectWriter().Prefixed(41, 2, [(0u, Dnp3Objects.AnalogOutput(2, 12))]).ToArray(), cts.Token);
        var poll = await m.IntegrityPollAsync(cts.Token);              // ordered after the NR operate on the same connection
        Assert.Equal(12, poll.Measurements.AnalogOutputs[0].Value, 6);
    }

    [Fact]
    public async Task LargeIntegrityPoll_UsesConfirmedMultiFragmentResponse()
    {
        var db = new Dnp3Database();
        for (ushort i = 0; i < 300; i++) db.AddAnalogInput(i, i * 0.25);
        var (os, m) = await Connect(db, new Dnp3OutstationOptions { MaxFragmentSize = 249 });
        await using var _ = os;
        await using var __ = m;
        using var cts = new CancellationTokenSource(Generous);
        var r = await m.IntegrityPollAsync(cts.Token);
        Assert.True(r.Fragments >= 6, $"{r.Fragments} fragments");    // 300 × 5 octets in ≤ 245-octet fragments
        Assert.Equal(300, r.Measurements.AnalogInputs.Count);
        Assert.Equal(299 * 0.25, r.Measurements.AnalogInputs[299].Value, 6);
        var next = await m.IntegrityPollAsync(cts.Token);              // sequence numbers stay consistent afterwards
        Assert.Equal(300, next.Measurements.AnalogInputs.Count);
    }

    [Fact]
    public async Task Unsolicited_TimeSync_AndDelayMeasure()
    {
        var db = SampleDatabase();
        var (os, m) = await Connect(db, new Dnp3OutstationOptions { NeedTimeAtStartup = true });
        await using var _ = os;
        await using var __ = m;
        using var cts = new CancellationTokenSource(Generous);
        var got = new TaskCompletionSource<Dnp3Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.UnsolicitedReceived += r => { if (r.Measurements.Events.Count > 0) got.TrySetResult(r); };

        var r0 = await m.IntegrityPollAsync(cts.Token);
        Assert.True(r0.Iin.HasFlag(Dnp3Iin.NeedTime));
        var target = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await m.WriteTimeAsync(target, cts.Token);
        Assert.False(m.LastIin.HasFlag(Dnp3Iin.NeedTime));
        Assert.InRange((os.Now - target).TotalSeconds, -1, 5);
        Assert.Equal(0, await m.DelayMeasureAsync(cts.Token));

        await m.EnableUnsolicitedAsync(ct: cts.Token);
        db.UpdateBinaryInput(0, true);
        var u = await got.Task.WaitAsync(Generous, cts.Token);
        var ev = Assert.Single(u.Measurements.Events);
        Assert.Equal((Dnp3PointType.BinaryInput, (ushort)0, 1.0), (ev.Type, ev.Index, ev.Value));
        // the unsolicited response was confirmed → the event is gone from the buffer
        for (int i = 0; i < 200 && db.EventCount(Dnp3PointClass.Class1) > 0; i++) await Task.Delay(10, cts.Token);
        Assert.Equal(0, db.EventCount(Dnp3PointClass.Class1));
    }

    [Fact]
    public async Task RtuSimulator_ServesPowerFlowThroughDnp3TagBridge()
    {
        var pf = PowerFlowSolver.Solve(TestCases.Case14());
        var rtu = new RtuSimulator(pf);
        var tags = new TagDatabase();
        rtu.UpdateTags(tags);
        var map = rtu.CreateDnp3Map();
        var db = map.CreateDatabase(tags);
        using var link = map.Attach(tags, db);
        var (os, m) = await Connect(db);
        await using var _ = os;
        await using var __ = m;
        using var cts = new CancellationTokenSource(Generous);

        await m.IntegrityPollAsync(cts.Token);
        var masterTags = new TagDatabase();
        rtu.RegisterTags(masterTags);
        Assert.Equal(map.Bindings.Count, map.ApplyTo(masterTags, m.Values));
        var vm4 = rtu.Points.Single(p => p.Name == "BUS4.VM");
        Assert.Equal(rtu.MeasuredValue(vm4), masterTags["BUS4.VM"].Value, 5);
        Assert.Equal(pf.Bus(4).Vm, masterTags["BUS4.VM"].Value, 2);
        Assert.Equal(1, masterTags["BR0.CLOSED"].Value);

        // a new operating point flows through tags → database events → event poll
        rtu.Refresh();
        rtu.UpdateTags(tags);
        var e = await m.EventPollAsync(ct: cts.Token);
        Assert.NotEmpty(e.Measurements.Events);
        map.ApplyTo(masterTags, m.Values);
        Assert.Equal(rtu.MeasuredValue(vm4), masterTags["BUS4.VM"].Value, 5);
    }

    [Fact]
    public void AddDnp3_RegistersMapDatabaseAndOptions()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddDnp3(new Dnp3OutstationOptions { LocalAddress = 10 })).BuildServiceProvider();
        var reg = sp.GetRequiredService<ElectricalModuleRegistry>().Modules;
        Assert.Contains("DNP3", reg);
        Assert.Contains("SCADA", reg);
        sp.GetRequiredService<TagDatabase>().Add("X", "kV");
        sp.GetRequiredService<Dnp3PointMap>().Add("X", Dnp3PointType.AnalogInput, 0);
        Assert.Single(sp.GetRequiredService<Dnp3Database>().Points(Dnp3PointType.AnalogInput));
        Assert.Equal((ushort)10, sp.GetRequiredService<Dnp3OutstationOptions>().LocalAddress);
    }
}
