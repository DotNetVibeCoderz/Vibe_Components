using System.Net;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.SCADA.Iec104;
using Electrical.SCADA.Modbus;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class ScadaTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b);

    // Modbus Application Protocol Specification v1.1b3, §6.3 (Read Holding Registers example: registers 108–110).
    [Fact]
    public void Modbus_ReadHoldingRegisters_MatchesSpecExample()
    {
        var req = ModbusRequest.ReadHoldingRegisters(0x6B, 3);
        Assert.Equal("03006B0003", Hex(ModbusPdu.EncodeRequest(req)));
        Assert.Equal("000100000006110300" + "6B0003", Hex(ModbusTcpAdu.Encode(1, 0x11, ModbusPdu.EncodeRequest(req))));

        var resp = ModbusPdu.DecodeResponse(Convert.FromHexString("0306022B00000064"), req);
        Assert.Equal(new ushort[] { 555, 0, 100 }, resp.Registers);
        Assert.Equal("0306022B00000064", Hex(ModbusPdu.EncodeResponse(resp)));

        var (tx, unit, pdu) = ModbusTcpAdu.Decode(Convert.FromHexString("00010000000611" + "03006B0003"));
        Assert.Equal((ushort)1, tx);
        Assert.Equal((byte)0x11, unit);
        Assert.Equal(req, ModbusPdu.DecodeRequest(pdu) with { });
    }

    // Spec §6.1 (Read Coils 20–38) and §6.11 (Write Multiple Coils 20–29).
    [Fact]
    public void Modbus_CoilExamples_PackBitsLsbFirst()
    {
        var req = ModbusRequest.ReadCoils(0x13, 0x13);
        Assert.Equal("0100130013", Hex(ModbusPdu.EncodeRequest(req)));
        var resp = ModbusPdu.DecodeResponse(Convert.FromHexString("0103CD6B05"), req);
        Assert.Equal(19, resp.Bits!.Length);
        Assert.True(resp.Bits[0]);   // coil 20 = 1 (0xCD bit 0)
        Assert.False(resp.Bits[1]);  // coil 21 = 0
        Assert.Equal("0103CD6B05", Hex(ModbusPdu.EncodeResponse(resp)));

        bool[] bits = [true, false, true, true, false, false, true, true, true, false]; // CD 01
        var w = ModbusRequest.WriteMultipleCoils(0x13, bits);
        Assert.Equal("0F0013000A02CD01", Hex(ModbusPdu.EncodeRequest(w)));
        var back = ModbusPdu.DecodeRequest(Convert.FromHexString("0F0013000A02CD01"));
        Assert.Equal(bits, back.Bits);
        Assert.Equal("0500ACFF00", Hex(ModbusPdu.EncodeRequest(ModbusRequest.WriteSingleCoil(0xAC, true))));
        Assert.Equal("1000010002040" + "00A0102", Hex(ModbusPdu.EncodeRequest(ModbusRequest.WriteMultipleRegisters(1, [0x000A, 0x0102]))));
    }

    [Fact]
    public void Modbus_Exceptions_AreEncodedAndRaised()
    {
        var map = new ModbusRegisterMap(16, 16, 16, 16);
        var r = map.Process(ModbusRequest.ReadHoldingRegisters(10, 10));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, r.Exception);
        Assert.Equal("8302", Hex(ModbusPdu.EncodeResponse(r)));

        var ex = Assert.Throws<ModbusException>(() => ModbusPdu.DecodeRequest([0x2B, 0x0E, 0x01, 0x00, 0x00]));
        Assert.Equal(ModbusExceptionCode.IllegalFunction, ex.Code);
        Assert.Equal(ModbusExceptionCode.IllegalDataValue,
            Assert.Throws<ModbusException>(() => ModbusPdu.DecodeRequest(Convert.FromHexString("0300000000"))).Code); // quantity 0

        var decoded = ModbusPdu.DecodeResponse(Convert.FromHexString("8302"), ModbusRequest.ReadHoldingRegisters(10, 10));
        Assert.True(decoded.IsException);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, decoded.Exception);
    }

    [Fact]
    public async Task ModbusTcp_Loopback_ReadWriteAndExceptions()
    {
        var map = new ModbusRegisterMap(100, 100, 100, 100);
        map.SetBit(ModbusTable.DiscreteInput, 3, true);
        map.SetFloat(ModbusTable.InputRegister, 10, 230.5f);
        var writes = new List<ModbusWriteEventArgs>();
        map.Written += writes.Add;

        await using var server = new ModbusTcpServer(map, IPAddress.Loopback, 0);
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new ModbusTcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, cts.Token);

        Assert.Equal((ushort)1, client.NextTransactionId);
        await client.WriteMultipleRegistersAsync(5, [11, 22, 33], ct: cts.Token);
        Assert.Equal(new ushort[] { 11, 22, 33 }, await client.ReadHoldingRegistersAsync(5, 3, ct: cts.Token));
        await client.WriteSingleRegisterAsync(0, 0xBEEF, ct: cts.Token);
        Assert.Equal(0xBEEF, map.GetRegister(ModbusTable.HoldingRegister, 0));
        await client.WriteSingleCoilAsync(7, true, ct: cts.Token);
        await client.WriteMultipleCoilsAsync(20, [true, false, true], ct: cts.Token);
        var coils = await client.ReadCoilsAsync(0, 24, ct: cts.Token);
        Assert.True(coils[7] && coils[20] && !coils[21] && coils[22]);
        Assert.True((await client.ReadDiscreteInputsAsync(0, 8, ct: cts.Token))[3]);
        Assert.Equal(230.5f, await client.ReadFloatAsync(ModbusTable.InputRegister, 10, ct: cts.Token));

        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadInputRegistersAsync(99, 5, ct: cts.Token));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.Code);

        Assert.Equal((ushort)10, client.NextTransactionId); // 9 requests sent
        Assert.Equal(9, server.RequestCount);
        Assert.Equal(4, writes.Count);

        // concurrent requests over one connection are serialized and matched by transaction id
        var reads = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.ReadHoldingRegistersAsync(5, 3, ct: cts.Token)));
        Assert.All(reads, r => Assert.Equal((ushort)22, r[1]));
    }

    [Fact]
    public void TagDatabase_ScalesRawValues_FlagsQuality_AndPublishesEvents()
    {
        var bus = new InMemoryGridEventBus();
        var seen = new List<TagValueChanged>();
        using var sub = bus.Subscribe<TagValueChanged>(seen.Add);
        var db = new TagDatabase(bus);
        // 0–32767 counts represent 0–150 kV
        var t = db.Add("SUB1.VLL", "kV", new TagAddress(1, ModbusTable.InputRegister, 0, TagDataType.Int16), new TagScaling(0, 32767, 0, 150));
        Assert.Equal(TagQuality.NotConnected, t.Quality);

        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.UpdateRaw("SUB1.VLL", 32767.0 * 138 / 150, ts);
        Assert.Equal(138, t.Value, 9);
        Assert.Equal(TagQuality.Good, t.Quality);
        Assert.Equal(ts, t.Timestamp);

        db.UpdateRaw("sub1.vll", -100, ts.AddSeconds(1)); // case-insensitive, out of raw range
        Assert.Equal(TagQuality.Uncertain, t.Quality);
        db.SetQuality("SUB1.VLL", TagQuality.CommFailure, ts.AddSeconds(2));
        Assert.Equal(3, seen.Count);
        Assert.Equal(TagQuality.CommFailure, seen[^1].Quality);

        // scan from an in-process map
        var map = new ModbusRegisterMap(10, 10, 10, 10);
        map.SetRegister(ModbusTable.InputRegister, 0, 16384);
        db.Scan(map, 1, ts.AddSeconds(3));
        Assert.Equal(16384 * 150.0 / 32767, t.Value, 9);
        Assert.Equal(TagQuality.Good, t.Quality);
    }

    [Fact]
    public void Alarms_FollowIsa182StateModel_WithDeadbandPriorityAndShelving()
    {
        var bus = new InMemoryGridEventBus();
        var published = new List<AlarmEvent>();
        using var sub = bus.Subscribe<AlarmEvent>(published.Add);
        var am = new AlarmManager(bus);
        var a = am.Define(new AlarmDefinition("BUS5.KV", HighHigh: 145, High: 142, Low: 131, LowLow: 124, Deadband: 1, Priority: AlarmPriority.Medium));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Empty(am.Evaluate("BUS5.KV", 138, t0));
        Assert.Equal(AlarmState.Normal, a.State);

        var e = Assert.Single(am.Evaluate("BUS5.KV", 142.5, t0.AddSeconds(1)));
        Assert.Equal((AlarmState.Normal, AlarmState.Unacknowledged, AlarmLimit.High, AlarmPriority.Medium), (e.PreviousState, e.State, e.Limit!.Value, e.Priority));

        Assert.True(am.Acknowledge("BUS5.KV", "operator", t0.AddSeconds(2)));
        Assert.Equal(AlarmState.Acknowledged, a.State);

        e = Assert.Single(am.Evaluate("BUS5.KV", 146, t0.AddSeconds(3))); // escalation H → HH re-annunciates
        Assert.Equal((AlarmState.Unacknowledged, AlarmLimit.HighHigh, AlarmPriority.High), (e.State, e.Limit!.Value, e.Priority));

        Assert.Empty(am.Evaluate("BUS5.KV", 141.5, t0.AddSeconds(4))); // below H but inside 1 kV deadband: still active
        Assert.True(a.ConditionActive);
        e = Assert.Single(am.Evaluate("BUS5.KV", 140.5, t0.AddSeconds(5)));
        Assert.Equal(AlarmState.ReturnToNormalUnacknowledged, e.State);
        Assert.Single(am.ActiveAlarms);
        am.Acknowledge("BUS5.KV", "operator", t0.AddSeconds(6));
        Assert.Equal(AlarmState.Normal, a.State);
        Assert.Empty(am.ActiveAlarms);

        am.Shelve("BUS5.KV", TimeSpan.FromMinutes(10), "operator", t0.AddSeconds(7));
        Assert.Empty(am.Evaluate("BUS5.KV", 120, t0.AddSeconds(8))); // suppressed while shelved
        Assert.Equal(AlarmState.Shelved, a.State);
        var ev = am.Evaluate("BUS5.KV", 120, t0.AddMinutes(11)); // shelf expired, LL condition re-annunciates
        Assert.Equal(AlarmState.Unacknowledged, ev[^1].State);
        Assert.Equal(AlarmLimit.LowLow, ev[^1].Limit);

        Assert.Equal(8, published.Count);
        Assert.Throws<ArgumentException>(() => am.Define(new AlarmDefinition("X", HighHigh: 1, High: 2)));
    }

    [Fact]
    public void Historian_SwingingDoor_CompressesWithinDeviation_AndInterpolates()
    {
        var h = new Historian();
        h.Configure("P", new HistorianTagOptions(CompressionMode.SwingingDoor, Deviation: 0.1));
        h.Configure("Q", new HistorianTagOptions(CompressionMode.Deadband, Deviation: 0.5));
        var t0 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var raw = new List<(DateTimeOffset T, double V)>();
        for (int i = 0; i <= 3600; i++)
        {
            double v = 50 + 10 * Math.Sin(2 * Math.PI * i / 600.0);
            raw.Add((t0.AddSeconds(i), v));
            h.Record("P", t0.AddSeconds(i), v);
            h.Record("Q", t0.AddSeconds(i), v);
        }
        Assert.Equal(3601, h.ReceivedCount("P"));
        Assert.True(h.CompressionRatio("P") > 10, $"ratio {h.CompressionRatio("P")}");
        foreach (var (t, v) in raw) Assert.True(Math.Abs(h.Interpolate("P", t) - v) <= 0.1 + 1e-9, $"error at {t}");

        Assert.True(h.ArchivedCount("Q") < 3601);
        var q = h.Query("P", t0.AddMinutes(10), t0.AddMinutes(20));
        Assert.All(q, s => Assert.InRange(s.Timestamp, t0.AddMinutes(10), t0.AddMinutes(20)));
        Assert.Equal(raw[^1].V, h.Query("P", t0, t0.AddHours(2))[^1].Value); // newest held sample is visible

        var ts = h.Resample("P", t0, t0.AddHours(1), TimeSpan.FromMinutes(1), "MW");
        Assert.Equal(60, ts.Count);
        Assert.Equal(50, ts.Mean(), 0); // full sine periods average to the offset
        Assert.True(double.IsNaN(h.Interpolate("P", t0.AddDays(1))));
        Assert.Throws<ArgumentException>(() => h.Record("P", t0, 1));
    }

    [Fact]
    public async Task RtuSimulator_ServesCase14OverModbus_Deterministically()
    {
        var pf = PowerFlowSolver.Solve(TestCases.Case14());
        var rtu = new RtuSimulator(pf);
        var twin = new RtuSimulator(pf);
        Assert.Equal(rtu.Map.ReadRegisters(ModbusTable.InputRegister, 0, 84), twin.Map.ReadRegisters(ModbusTable.InputRegister, 0, 84));
        var noiseFree = new RtuSimulator(pf, new RtuOptions { VoltageNoisePu = 0, AngleNoiseDeg = 0, PowerNoiseMw = 0 });
        int bus14 = pf.Network.IndexOf(14);
        Assert.Equal((float)pf.Bus(14).Vm, noiseFree.Map.GetFloat(ModbusTable.InputRegister, noiseFree.BusAddress(bus14)));

        await using var server = new ModbusTcpServer(rtu.Map, IPAddress.Loopback, 0);
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = new ModbusTcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, cts.Token);

        float vm14 = await client.ReadFloatAsync(ModbusTable.InputRegister, rtu.BusAddress(bus14), ct: cts.Token);
        Assert.Equal(1.036, vm14, 0.006); // MATPOWER case14: |V14| = 1.036 pu, noise σ = 0.001 pu
        float pf0 = await client.ReadFloatAsync(ModbusTable.InputRegister, rtu.BranchAddress(0), ct: cts.Token);
        Assert.Equal(pf.Branches[0].PFrom, pf0, 0.3);

        var db = new TagDatabase();
        rtu.RegisterTags(db);
        Assert.Equal(rtu.Points.Count, db.Count);
        Assert.Equal(db.Count, await db.ScanAsync(client, cts.Token));
        Assert.All(db.All, t => Assert.Equal(TagQuality.Good, t.Quality));
        Assert.Equal(1, db["BR0.CLOSED"].Value);
        foreach (var p in rtu.Points.Where(p => p.Unit == "pu")) Assert.Equal(p.TrueValue, db[p.Name].Value, 0.006);
    }

    [Fact]
    public void Iec104_ControlFrames_MatchStandardBytes()
    {
        Assert.Equal("680407000000", Hex(Iec104Codec.Encode(Apdu.U(UFunction.StartDtAct))));
        Assert.Equal("68040B000000", Hex(Iec104Codec.Encode(Apdu.U(UFunction.StartDtCon))));
        Assert.Equal("680443000000", Hex(Iec104Codec.Encode(Apdu.U(UFunction.TestFrAct))));
        Assert.Equal("680401000A00", Hex(Iec104Codec.Encode(Apdu.S(5))));
        Assert.Equal(UFunction.StopDtCon, Iec104Codec.Decode(Convert.FromHexString("680423000000")).Function);
        var s = Iec104Codec.Decode(Convert.FromHexString("680401000A00"));
        Assert.Equal((ApciFormat.S, (ushort)5), (s.Format, s.ReceiveSequence));
        Assert.Throws<InvalidDataException>(() => Iec104Codec.Decode(Convert.FromHexString("670407000000")));
    }

    [Fact]
    public void Iec104_MeasuredFloatAndCommands_RoundTrip()
    {
        var asdu = new Asdu(TypeId.M_ME_NC_1, CauseOfTransmission.Spontaneous, 1, [new InformationObject(0x004001, 1.5)]);
        byte[] frame = Iec104Codec.Encode(Apdu.I(2, 1, asdu));
        // 68 len=18 | N(S)=2 → 04 00 | N(R)=1 → 02 00 | type 13, VSQ 1, COT 3, OA 0, CA 0001 | IOA 01 40 00 | 1.5f LE | QDS
        Assert.Equal("6812" + "04000200" + "0D01030001 00".Replace(" ", "") + "014000" + "0000C03F" + "00", Hex(frame));
        var d = Iec104Codec.Decode(frame, out int used);
        Assert.Equal(frame.Length, used);
        Assert.Equal((ApciFormat.I, (ushort)2, (ushort)1), (d.Format, d.SendSequence, d.ReceiveSequence));
        Assert.Equal(1.5, d.Asdu!.Objects[0].Value);
        Assert.Equal(0x004001u, d.Asdu.Objects[0].Address);

        // CP56Time2a: 2024-01-02 (Tuesday) 03:04:05.678 UTC
        var t = new DateTimeOffset(2024, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);
        var cp = new byte[7];
        Cp56Time2a.Encode(t, cp);
        Assert.Equal("2E160403420118", Hex(cp));
        Assert.Equal(t, Cp56Time2a.Decode(cp));

        var cmd = new Asdu(TypeId.C_SC_TA_1, CauseOfTransmission.Activation, 7,
            [new InformationObject(5001, 1, Time: t, Select: true, Qualifier: 1)], OriginatorAddress: 3);
        var back = Iec104Codec.DecodeAsdu(Iec104Codec.EncodeAsdu(cmd));
        Assert.Equal((TypeId.C_SC_TA_1, CauseOfTransmission.Activation, (ushort)7, (byte)3), (back.Type, back.Cause, back.CommonAddress, back.OriginatorAddress));
        var o = back.Objects[0];
        Assert.True(o.State && o.Select);
        Assert.Equal((byte)1, o.Qualifier);
        Assert.Equal(t, o.Time);
        Assert.Equal(0x85, Iec104Codec.EncodeAsdu(cmd)[9]); // SCO = S/E | QU=1 | SCS=1

        var sp = new Asdu(TypeId.M_SP_TB_1, CauseOfTransmission.Spontaneous, 1,
            [new InformationObject(100, 1, Iec104Quality.Invalid, t), new InformationObject(101, 0, Time: t)], Negative: false, Test: true);
        var spBack = Iec104Codec.DecodeAsdu(Iec104Codec.EncodeAsdu(sp));
        Assert.True(spBack.Test);
        Assert.Equal(Iec104Quality.Invalid, spBack.Objects[0].Quality);
        Assert.False(spBack.Objects[1].State);

        // SQ = 1 sequence of 3 single points starting at IOA 10
        var seq = Iec104Codec.DecodeAsdu(Convert.FromHexString("0183140001000A0000" + "01" + "00" + "81"));
        Assert.Equal(new uint[] { 10, 11, 12 }, seq.Objects.Select(x => x.Address));
        Assert.Equal(CauseOfTransmission.InterrogatedByStation, seq.Cause);
        Assert.True(seq.Objects[2].State);
        Assert.Equal(Iec104Quality.Invalid, seq.Objects[2].Quality);

        var counters = new Iec104Sequence();
        counters.NextI(asdu);
        var second = Iec104Codec.Decode(counters.NextI(asdu));
        Assert.Equal((ushort)1, second.SendSequence);
        counters.OnReceived(Apdu.I(0, 2, asdu));
        Assert.Equal("680401000200", Hex(counters.Acknowledge()));
        Assert.Throws<InvalidDataException>(() => counters.OnReceived(Apdu.I(5, 2, asdu)));
    }

    [Fact]
    public void AddScada_WiresTagsAlarmsAndHistorianThroughEventBus()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddScada().AddScada()).BuildServiceProvider();
        Assert.Contains("SCADA", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
        var tags = sp.GetRequiredService<TagDatabase>();
        var alarms = sp.GetRequiredService<AlarmManager>();
        var hist = sp.GetRequiredService<Historian>();
        var events = new List<AlarmEvent>();
        using var sub = sp.GetRequiredService<IGridEventBus>().Subscribe<AlarmEvent>(events.Add);

        tags.Add("LINE1.LOAD", "%");
        alarms.Define(new AlarmDefinition("LINE1.LOAD", High: 100, HighHigh: 120, Priority: AlarmPriority.High));
        var t0 = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        tags.UpdateValue("LINE1.LOAD", 80, t0);
        tags.UpdateValue("LINE1.LOAD", 125, t0.AddSeconds(1));

        var e = Assert.Single(events);
        Assert.Equal((AlarmLimit.HighHigh, AlarmPriority.Critical), (e.Limit!.Value, e.Priority));
        Assert.Equal(102.5, hist.Interpolate("LINE1.LOAD", t0.AddSeconds(0.5)), 9);
    }
}
