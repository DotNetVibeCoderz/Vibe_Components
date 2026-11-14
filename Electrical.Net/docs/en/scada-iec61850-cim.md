# SCADA, IEC 61850 and CIM

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/scada-iec61850-cim.md)

Packages: `Electrical.SCADA` (namespaces `Electrical.SCADA`, `Electrical.SCADA.Modbus`, `Electrical.SCADA.Iec104`), `Electrical.IEC61850` and
`Electrical.CIM`.

## SCADA

### Modbus TCP and the RTU simulator

`ModbusRegisterMap` holds coils, discrete inputs, holding and input registers (with `SetFloat`/`GetFloat` helpers and a `Written` event).
`ModbusTcpServer` serves a map; `ModbusTcpClient` reads and writes it (function codes 1, 2, 3, 4, 5, 6, 15, 16; concurrent requests on one
connection are serialised and matched by transaction id; Modbus exceptions are raised as `ModbusException`). `RtuSimulator` publishes a
power-flow result as Float32 input registers with seeded measurement noise.

```csharp
var pf = PowerFlowSolver.Solve(TestCases.Case14());
var rtu = new RtuSimulator(pf);                                 // BUSn.VM/VA/KV, BRk.PF/QF/PT/QT/LOAD, GENg.P/Q, BRk.CLOSED
await using var server = new ModbusTcpServer(rtu.Map, IPAddress.Loopback, port: 1502);
server.Start();

await using var client = new ModbusTcpClient();
await client.ConnectAsync("127.0.0.1", 1502);
float vm14 = await client.ReadFloatAsync(ModbusTable.InputRegister, rtu.BusAddress(pf.Network.IndexOf(14)));
ushort[] regs = await client.ReadHoldingRegistersAsync(0, 10);

var tags = new TagDatabase();
rtu.RegisterTags(tags);
int scanned = await tags.ScanAsync(client);
Console.WriteLine(tags["BUS14.VM"]);
```

Register layout (`RtuOptions`): buses at `BusBaseAddress + 6·i` (VM, VA, KV as Float32 pairs), branches at `BranchBaseAddress + 10·k`
(2000 by default: PF, QF, PT, QT, LOAD), generators at `GeneratorBaseAddress + 4·g` (6000: P, Q), breaker states as discrete inputs/coils.
`Refresh()` redraws the noise; `ResetNoise()` restarts the deterministic sequence. CLI: `eln rtu --case case14 --port 1502`.

`ModbusPdu` and `ModbusTcpAdu` are the stateless codecs (tested against the Modbus Application Protocol v1.1b3 examples).

### Tags, alarms and historian

```csharp
var tags = new TagDatabase();
var alarms = new AlarmManager();
var historian = new Historian(new HistorianTagOptions(CompressionMode.SwingingDoor, Deviation: 0.5));

tags.Add("LINE1.LOAD", "%");
alarms.Define(new AlarmDefinition("LINE1.LOAD", HighHigh: 120, High: 100, Deadband: 2, Priority: AlarmPriority.High));
using var a = alarms.Attach(tags);
using var h = historian.Attach(tags);
alarms.AlarmChanged += e => Console.WriteLine($"{e.Timestamp:T} {e.Tag} {e.State} {e.Limit} {e.Priority}: {e.Message}");

var t0 = DateTimeOffset.UtcNow;
tags.UpdateValue("LINE1.LOAD", 80, t0);
tags.UpdateValue("LINE1.LOAD", 125, t0.AddSeconds(1));    // → HH, priority escalated to Critical
alarms.Acknowledge("LINE1.LOAD", user: "operator1");
var trend = historian.Resample("LINE1.LOAD", t0, t0.AddMinutes(1), TimeSpan.FromSeconds(10));
```

- `Tag`: raw value, engineering value via `TagScaling` (linear raw → EU), `TagQuality` (Good, out-of-range raw values flagged), timestamp.
  `TagAddress` maps a tag to a Modbus unit/table/address/data type (Bool, Int16, UInt16, Int32, UInt32, Float32). `Scan` (in-process map)
  and `ScanAsync` (TCP client) poll all addressed tags. Changes raise `TagValueChanged` on the event bus.
- `AlarmManager` follows the ISA-18.2 state model (normal, unacknowledged, acknowledged, return-to-normal unacknowledged, shelved),
  HH/H/L/LL limits with deadband, priority escalation for HH/LL, acknowledgement with user, shelving with expiry; `AlarmEvent`s are published
  on the bus.
- `Historian` stores per-tag samples with `None`, `Deadband` or `SwingingDoor` compression (Bristol 1990; interpolation stays within the
  deviation), `MaxInterval` heartbeats, `Query`, `Interpolate`, `Resample` to `TimeSeries` and `CompressionRatio`.
- DI: `AddScada(historianDefaults)` registers `TagDatabase`, `AlarmManager` and `Historian` wired through `IGridEventBus`.

### IEC 60870-5-104

```csharp
var asdu = new Asdu(TypeId.M_ME_NC_1, CauseOfTransmission.Spontaneous, CommonAddress: 1,
    [new InformationObject(Address: 16385, Value: 1.5)]);
byte[] frame = Iec104Codec.Encode(Apdu.I(sendSequence: 2, receiveSequence: 1, asdu));
Apdu back = Iec104Codec.Decode(frame);
byte[] startDt = Iec104Codec.Encode(Apdu.U(UFunction.StartDtAct));
```

I-, S- and U-format APDUs, common measured/command type ids (single point, measured float with/without CP56Time2a, single command),
`Cp56Time2a` timestamps and `Iec104Sequence` counters. The tests compare control frames and a measured-float frame byte by byte. This is a
codec: TCP session handling (t1–t3 timers, k/w windows) is left to the application; a full client is planned for v1.4.

## IEC 61850

### SCL

```csharp
var scl = SclParser.Load("substation.scd");
foreach (var ied in scl.Ieds)
    foreach (var da in scl.DataModel(ied).Take(5))
        Console.WriteLine($"{da.Reference} [{da.BType}] = {da.Value}");
var r = ObjectReference.Parse("IED1LD0/XCBR1.Pos.stVal[ST]");
Console.WriteLine(LogicalNodeCatalog.Describe("XCBR"));
```

`SclDocument` exposes IEDs (access points, logical devices, logical nodes, DAI values, datasets, GSE/SMV/report control blocks), the
communication section (subnetworks, connected APs, GSE addresses with MinTime/MaxTime) and the data type templates (LNodeType, DOType, DAType,
EnumType). `DataModel(ied)` flattens the model into `SclDataAttribute`s with instance values overriding template defaults.
`LogicalNodeCatalog` describes common LN classes in English or Indonesian (`Describe(lnClass, indonesian: true)`).

### GOOSE and Sampled Values

```csharp
var state = new GoosePublisherState();
state.OnStateChange(DateTimeOffset.UtcNow);                       // stNum++, sqNum = 0
var pdu = state.CreatePdu("IED1CTRL/LLN0$GO$gcbTrip", "IED1CTRL/LLN0$dsTrip",
    [new MmsBoolean(true), MmsBitString.FromDoublePoint(DoublePoint.Off)]);
byte[] frame = GooseCodec.Encode(new GooseFrame { Pdu = pdu, AppId = 0x0001, Vlan = new VlanTag(4, 10) });
GooseFrame decoded = GooseCodec.Decode(frame);
var (intervalMs, tal) = state.NextRetransmission();               // 2, 4, 8 … ms up to MaxTimeMs

var sv = SampledValuesCodec.Simulate("MU01", smpCnt: 0, samplesPerCycle: 80, iRmsA: 400, vRmsV: 66_395);
byte[] svFrame = SampledValuesCodec.Encode(new SvFrame { Asdus = [sv] });
```

- GOOSE: full Ethernet frame (multicast 01-0C-CD-01-xx-xx, optional 802.1Q tag, EtherType 0x88B8, APPID, length) with the ASN.1 BER
  `goosePdu`; MMS data types (boolean, integers, float, double, strings, UTC time, bit strings incl. double point and quality, structures,
  arrays). `GooseRetransmission` yields the 2 → 4 → 8 … → MaxTime schedule and time-allowed-to-live.
- Sampled Values: IEC 61850-9-2LE ASDUs (4 currents at 1 mA, 4 voltages at 10 mV resolution, quality flags), `FromPhysical` and a 3-phase
  `Simulate` helper; multi-ASDU frames.
- `Ber` encoder/decoder, `MacAddress`, `VlanTag`, `EthernetFrame`.

Raw-socket publishing/subscribing and an MMS client/server are not included; send and capture the frames with a raw Ethernet / packet
library of your choice.

## CIM / CGMES

```csharp
var net = TestCases.Case14();
string rdf = CimExporter.ExportString(net);                       // merged EQ + TP + SSH + SV
var profiles = CimExporter.ExportProfiles(net);                   // four CGMES-style documents
profiles.Equipment.Save("case14_EQ.xml");

var back = CimImporter.Import(rdf);
var detailed = CimImporter.ImportDetailed(profiles.All, new CimImportOptions { BaseMva = 100 });
foreach (var w in detailed.Warnings) Console.WriteLine(w);
Console.WriteLine(PowerFlowSolver.Solve(back).LossesMw);          // 13.393 MW, identical to the original
```

Mapping: bus → ConnectivityNode (+ TopologicalNode, VoltageLevel, Substation, BaseVoltage, Location); line → ACLineSegment (r, x in Ω, bch in
S, length); transformer → PowerTransformer with two PowerTransformerEnds (+ RatioTapChanger / PhaseTapChangerLinear); generator →
SynchronousMachine + GeneratingUnit + RegulatingControl (SSH p/q in load sign convention); load → EnergyConsumer (+
LoadResponseCharacteristic for ZIP); shunt → LinearShuntCompensator; ratings → OperationalLimitSet/ApparentPowerLimit.

The importer accepts merged or split EQ/TP/SSH/SV documents, merges ConnectivityNodes joined by closed switches (Switch, Breaker,
Disconnector, LoadBreakSwitch, Fuse, Jumper) or by a shared TopologicalNode, and supports ExternalNetworkInjection, Conform/NonConform loads
and SvVoltage. Optional `eln:` extension attributes (bus ids/types, base MVA, costs, inertia) make the round trip lossless; without them bus
types are derived. Export is deterministic (`ScenarioTime` default 2026-01-01). CLI: `eln convert --case case14 --out grid.xml`.

Validated: case14 and case9 round trips give identical power flows; case33bw without extensions still gives 202.7 kW losses; branch 1-2 of
case14 exports r = 3.6907 Ω and bch = 2.77253e-4 S; topology processing merges closed breakers and separates open disconnectors.

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/scada-iec61850-cim.md)*
