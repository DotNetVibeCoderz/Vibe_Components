# SCADA, IEC 61850, dan CIM

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/scada-iec61850-cim.md)

Paket: `Electrical.SCADA` (namespace `Electrical.SCADA`, `Electrical.SCADA.Modbus`, `Electrical.SCADA.Iec104`), `Electrical.IEC61850`, dan
`Electrical.CIM`.

## SCADA

### Modbus TCP dan simulator RTU

`ModbusRegisterMap` menyimpan coil, discrete input, holding register, dan input register (dengan helper `SetFloat`/`GetFloat` dan event
`Written`). `ModbusTcpServer` melayani sebuah map; `ModbusTcpClient` membaca dan menulisnya (kode fungsi 1, 2, 3, 4, 5, 6, 15, 16; permintaan
bersamaan pada satu koneksi diserialkan dan dicocokkan dengan transaction id; exception Modbus dilempar sebagai `ModbusException`).
`RtuSimulator` memublikasikan hasil aliran daya sebagai input register Float32 dengan derau pengukuran ber-seed.

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

Tata letak register (`RtuOptions`): bus di `BusBaseAddress + 6·i` (VM, VA, KV sebagai pasangan Float32), cabang di `BranchBaseAddress + 10·k`
(default 2000: PF, QF, PT, QT, LOAD), generator di `GeneratorBaseAddress + 4·g` (6000: P, Q), status pemutus sebagai discrete input/coil.
`Refresh()` menggambar ulang derau; `ResetNoise()` memulai ulang urutan deterministik. CLI: `eln rtu --case case14 --port 1502`.

`ModbusPdu` dan `ModbusTcpAdu` adalah codec tanpa state (diuji terhadap contoh Modbus Application Protocol v1.1b3).

### Tag, alarm, dan historian

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
tags.UpdateValue("LINE1.LOAD", 125, t0.AddSeconds(1));    // → HH, prioritas naik menjadi Critical
alarms.Acknowledge("LINE1.LOAD", user: "operator1");
var trend = historian.Resample("LINE1.LOAD", t0, t0.AddMinutes(1), TimeSpan.FromSeconds(10));
```

- `Tag`: nilai mentah, nilai teknik lewat `TagScaling` (linear mentah → EU), `TagQuality` (nilai mentah di luar rentang ditandai), stempel waktu.
  `TagAddress` memetakan tag ke unit/tabel/alamat/tipe data Modbus (Bool, Int16, UInt16, Int32, UInt32, Float32). `Scan` (map in-process) dan
  `ScanAsync` (klien TCP) membaca semua tag yang beralamat. Perubahan memunculkan `TagValueChanged` di event bus.
- `AlarmManager` mengikuti model state ISA-18.2 (normal, belum diakui, diakui, kembali normal belum diakui, di-shelve), batas HH/H/L/LL dengan
  deadband, eskalasi prioritas untuk HH/LL, pengakuan beserta nama pengguna, shelving dengan kedaluwarsa; `AlarmEvent` dipublikasikan di bus.
- `Historian` menyimpan sampel per tag dengan kompresi `None`, `Deadband`, atau `SwingingDoor` (Bristol 1990; interpolasi tetap dalam deviasi),
  heartbeat `MaxInterval`, `Query`, `Interpolate`, `Resample` ke `TimeSeries`, dan `CompressionRatio`.
- DI: `AddScada(historianDefaults)` mendaftarkan `TagDatabase`, `AlarmManager`, dan `Historian` yang terhubung lewat `IGridEventBus`.

### IEC 60870-5-104

```csharp
var asdu = new Asdu(TypeId.M_ME_NC_1, CauseOfTransmission.Spontaneous, CommonAddress: 1,
    [new InformationObject(Address: 16385, Value: 1.5)]);
byte[] frame = Iec104Codec.Encode(Apdu.I(sendSequence: 2, receiveSequence: 1, asdu));
Apdu back = Iec104Codec.Decode(frame);
byte[] startDt = Iec104Codec.Encode(Apdu.U(UFunction.StartDtAct));
```

APDU format I, S, dan U, type id umum untuk pengukuran/perintah (single point, measured float dengan/tanpa CP56Time2a, single command),
stempel waktu `Cp56Time2a`, dan penghitung `Iec104Sequence`. Tes membandingkan frame kendali dan frame measured-float byte per byte. Ini adalah
codec: penanganan sesi TCP (timer t1–t3, jendela k/w) diserahkan ke aplikasi; klien lengkap direncanakan untuk v1.4.

## IEC 61850

### SCL

```csharp
var scl = SclParser.Load("substation.scd");
foreach (var ied in scl.Ieds)
    foreach (var da in scl.DataModel(ied).Take(5))
        Console.WriteLine($"{da.Reference} [{da.BType}] = {da.Value}");
var r = ObjectReference.Parse("IED1LD0/XCBR1.Pos.stVal[ST]");
Console.WriteLine(LogicalNodeCatalog.Describe("XCBR", indonesian: true));
```

`SclDocument` menyediakan IED (access point, logical device, logical node, nilai DAI, dataset, control block GSE/SMV/report), bagian
komunikasi (subnetwork, connected AP, alamat GSE dengan MinTime/MaxTime), dan data type template (LNodeType, DOType, DAType, EnumType).
`DataModel(ied)` meratakan model menjadi `SclDataAttribute` dengan nilai instance yang menimpa default template. `LogicalNodeCatalog`
mendeskripsikan kelas LN umum dalam bahasa Inggris atau Indonesia (`Describe(lnClass, indonesian: true)`).

### GOOSE dan Sampled Values

```csharp
var state = new GoosePublisherState();
state.OnStateChange(DateTimeOffset.UtcNow);                       // stNum++, sqNum = 0
var pdu = state.CreatePdu("IED1CTRL/LLN0$GO$gcbTrip", "IED1CTRL/LLN0$dsTrip",
    [new MmsBoolean(true), MmsBitString.FromDoublePoint(DoublePoint.Off)]);
byte[] frame = GooseCodec.Encode(new GooseFrame { Pdu = pdu, AppId = 0x0001, Vlan = new VlanTag(4, 10) });
GooseFrame decoded = GooseCodec.Decode(frame);
var (intervalMs, tal) = state.NextRetransmission();               // 2, 4, 8 … ms hingga MaxTimeMs

var sv = SampledValuesCodec.Simulate("MU01", smpCnt: 0, samplesPerCycle: 80, iRmsA: 400, vRmsV: 66_395);
byte[] svFrame = SampledValuesCodec.Encode(new SvFrame { Asdus = [sv] });
```

- GOOSE: frame Ethernet lengkap (multicast 01-0C-CD-01-xx-xx, tag 802.1Q opsional, EtherType 0x88B8, APPID, panjang) dengan `goosePdu` ASN.1 BER;
  tipe data MMS (boolean, integer, float, double, string, waktu UTC, bit string termasuk double point dan quality, structure, array).
  `GooseRetransmission` menghasilkan jadwal 2 → 4 → 8 … → MaxTime dan time-allowed-to-live.
- Sampled Values: ASDU IEC 61850-9-2LE (4 arus resolusi 1 mA, 4 tegangan resolusi 10 mV, flag quality), `FromPhysical` dan helper `Simulate`
  tiga fasa; frame multi-ASDU.
- Encoder/decoder `Ber`, `MacAddress`, `VlanTag`, `EthernetFrame`.

Publikasi/langganan raw-socket dan klien/server MMS tidak disertakan; kirim dan tangkap frame dengan pustaka raw Ethernet / packet pilihan Anda.

## CIM / CGMES

```csharp
var net = TestCases.Case14();
string rdf = CimExporter.ExportString(net);                       // EQ + TP + SSH + SV digabung
var profiles = CimExporter.ExportProfiles(net);                   // empat dokumen gaya CGMES
profiles.Equipment.Save("case14_EQ.xml");

var back = CimImporter.Import(rdf);
var detailed = CimImporter.ImportDetailed(profiles.All, new CimImportOptions { BaseMva = 100 });
foreach (var w in detailed.Warnings) Console.WriteLine(w);
Console.WriteLine(PowerFlowSolver.Solve(back).LossesMw);          // 13,393 MW, identik dengan aslinya
```

Pemetaan: bus → ConnectivityNode (+ TopologicalNode, VoltageLevel, Substation, BaseVoltage, Location); saluran → ACLineSegment (r, x dalam Ω,
bch dalam S, panjang); trafo → PowerTransformer dengan dua PowerTransformerEnd (+ RatioTapChanger / PhaseTapChangerLinear); generator →
SynchronousMachine + GeneratingUnit + RegulatingControl (p/q SSH dalam konvensi tanda beban); beban → EnergyConsumer (+
LoadResponseCharacteristic untuk ZIP); shunt → LinearShuntCompensator; rating → OperationalLimitSet/ApparentPowerLimit.

Importer menerima dokumen EQ/TP/SSH/SV yang digabung atau terpisah, menggabungkan ConnectivityNode yang terhubung oleh saklar tertutup (Switch,
Breaker, Disconnector, LoadBreakSwitch, Fuse, Jumper) atau oleh TopologicalNode yang sama, dan mendukung ExternalNetworkInjection, beban
Conform/NonConform, serta SvVoltage. Atribut ekstensi `eln:` opsional (id/tipe bus, base MVA, biaya, inersia) membuat round trip tanpa
kehilangan; tanpa ekstensi, tipe bus diturunkan. Ekspor bersifat deterministik (`ScenarioTime` default 2026-01-01). CLI:
`eln convert --case case14 --out grid.xml`.

Tervalidasi: round trip case14 dan case9 menghasilkan aliran daya identik; case33bw tanpa ekstensi tetap menghasilkan rugi 202,7 kW; cabang
1-2 case14 diekspor dengan r = 3,6907 Ω dan bch = 2,77253e-4 S; pemrosesan topologi menggabungkan breaker tertutup dan memisahkan
disconnector terbuka.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/scada-iec61850-cim.md)*
