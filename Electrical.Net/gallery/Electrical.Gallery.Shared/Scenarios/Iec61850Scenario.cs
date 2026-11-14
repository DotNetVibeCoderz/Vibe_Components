using System.Globalization;
using Electrical.IEC61850;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;

namespace Electrical.Gallery.Shared;

/// <summary>Decoded field of a frame (BER tag, name, value).</summary>
public sealed record FrameField(int Depth, string Tag, string Name, string Value);

/// <summary>Node of the SCL model tree.</summary>
public sealed record SclNode(int Depth, string Kind, string Name, string? Detail);

public sealed record Iec61850DemoResult(
    string Kind, byte[] Frame, IReadOnlyList<HexDump.Segment> Segments, IReadOnlyList<FrameField> Fields, bool RoundTripOk,
    string ChartSvg, string? PhasorSvg, IReadOnlyList<SclNode> SclTree, IReadOnlyList<Kpi> Kpis);

/// <summary>GOOSE / Sampled Values frame builder + inspector and SCL model browser (Electrical.IEC61850).</summary>
public static class Iec61850Scenario
{
    private static readonly DateTimeOffset TripTime = new(2026, 11, 14, 8, 30, 0, 125, TimeSpan.Zero);

    public static Iec61850DemoResult Run(string kind = "goose", bool breakerOpen = true, int retransmission = 0, bool vlan = true,
        double currentA = 400, double phiDeg = 25, string lang = "en")
    {
        bool id = lang == "id";
        return kind == "sv" ? Sv(currentA, phiDeg, vlan, id) : Goose(breakerOpen, retransmission, vlan, id);
    }

    private static List<HexDump.Segment> EthSegments(bool vlan, int pduLength, int frameLength)
    {
        var s = new List<HexDump.Segment>
        {
            new(0, 6, "dst", new("Destination MAC", "MAC tujuan")),
            new(6, 12, "src", new("Source MAC", "MAC sumber")),
        };
        int p = 12;
        if (vlan) { s.Add(new(12, 16, "vlan", new("802.1Q tag", "Tag 802.1Q"))); p = 16; }
        s.Add(new(p, p + 2, "type", new("EtherType", "EtherType")));
        s.Add(new(p + 2, p + 10, "appid", new("APPID · Length · Reserved", "APPID · Panjang · Cadangan")));
        s.Add(new(p + 10, p + 10 + pduLength, "pdu", new("APDU (BER)", "APDU (BER)")));
        if (frameLength > p + 10 + pduLength) s.Add(new(p + 10 + pduLength, frameLength, "pad", new("Padding", "Padding")));
        return s;
    }

    private static Iec61850DemoResult Goose(bool open, int retrans, bool vlan, bool id)
    {
        var state = new GoosePublisherState();
        state.OnStateChange(TripTime);
        if (!open) state.OnStateChange(TripTime.AddSeconds(4));
        for (int i = 0; i < retrans; i++) state.NextRetransmission();
        var pos = open ? DoublePoint.Off : DoublePoint.On;
        var pdu = state.CreatePdu("IED1CTRL/LLN0$GO$gcbTrip", "IED1CTRL/LLN0$dsTrip",
        [
            MmsBitString.FromDoublePoint(pos), new MmsBitString([0x00, 0x00], 13), new MmsUtcTime(state.LastChange),
            new MmsBoolean(open), new MmsFloat(open ? 0f : 412.5f),
        ], goId: "IED1_TRIP", confRev: 3);
        var frameRec = new GooseFrame { Vlan = vlan ? new VlanTag(4, 10) : null, AppId = 0x0001, Pdu = pdu };
        byte[] frame = GooseCodec.Encode(frameRec);
        byte[] pduBytes = GooseCodec.EncodePdu(pdu);
        var back = GooseCodec.Decode(frame);
        bool ok = back.Pdu.Equals(pdu) && back.AppId == frameRec.AppId;

        var fields = new List<FrameField>
        {
            new(0, "", id ? "Tujuan" : "Destination", back.Destination.ToString()),
            new(0, "", id ? "Sumber" : "Source", back.Source.ToString()),
        };
        if (back.Vlan is VlanTag v) fields.Add(new(0, "8100", "VLAN", $"id {v.VlanId} · priority {v.Priority}"));
        fields.Add(new(0, "88B8", "EtherType", "GOOSE"));
        fields.Add(new(0, "", "APPID", $"0x{back.AppId:X4}"));
        WalkGoose(new BerReader(pduBytes).Read(), 0, fields);

        var sched = state.Schedule;
        var times = sched.TransmissionTimes(10);
        string chart = SvgCharts.Bar(Enumerable.Range(0, 10).Select(i => i == 0 ? "st" : $"sq{i}").ToList(),
            [new ChartSeries(id ? "interval (ms)" : "interval (ms)", Enumerable.Range(0, 10).Select(i => i == 0 ? sched.MinTimeMs : sched.IntervalAfter((uint)i)).ToList(), SvgTheme.Var("eln-accent"))],
            new ChartOptions
            {
                Width = 640, Height = 230, YLabel = id ? "jeda ke pesan berikut (ms)" : "gap to next message (ms)", ShowLegend = false, ValueFormat = "0",
                XLabel = id ? $"retransmisi setelah perubahan status (total {F(times[^1], "0")} ms)" : $"retransmissions after a state change (total {F(times[^1], "0")} ms)",
            });

        var kpis = new List<Kpi>
        {
            new(new("Breaker", "Pemutus"), open ? "OPEN" : "CLOSED", null, new("XCBR1.Pos.stVal", "XCBR1.Pos.stVal"), open ? "alarm" : "ok"),
            new(new("stNum / sqNum", "stNum / sqNum"), $"{pdu.StNum} / {pdu.SqNum}", null, new("state / sequence", "status / urutan")),
            new(new("Time allowed to live", "Batas waktu hidup"), pdu.TimeAllowedToLiveMs.ToString(CultureInfo.InvariantCulture), "ms", new("2 × next interval", "2 × interval berikut")),
            new(new("Frame", "Frame"), frame.Length.ToString(CultureInfo.InvariantCulture), "bytes", new($"APDU {pduBytes.Length} B", $"APDU {pduBytes.Length} B")),
            new(new("Round trip", "Bolak-balik"), ok ? "MATCH" : "DIFF", null, new("decode(encode(x)) == x", "decode(encode(x)) == x"), ok ? "ok" : "alarm"),
        };
        return new Iec61850DemoResult("goose", frame, EthSegments(vlan, pduBytes.Length, frame.Length), fields, ok, chart, null, SclTree(), kpis);
    }

    private static readonly Dictionary<int, string> GooseTags = new()
    {
        [0x80] = "gocbRef", [0x81] = "timeAllowedToLive", [0x82] = "datSet", [0x83] = "goID", [0x84] = "t", [0x85] = "stNum", [0x86] = "sqNum",
        [0x87] = "simulation", [0x88] = "confRev", [0x89] = "ndsCom", [0x8A] = "numDatSetEntries", [0xAB] = "allData",
    };

    private static void WalkGoose(BerTlv pdu, int depth, List<FrameField> fields)
    {
        fields.Add(new(depth, $"{pdu.Tag:X2}", "goosePdu", $"{pdu.Value.Length} B"));
        foreach (var t in pdu.Children())
        {
            string name = GooseTags.TryGetValue(t.Tag, out var n) ? n : "?";
            string value = t.Tag switch
            {
                0x80 or 0x82 or 0x83 => t.AsString(),
                0x84 => UtcTime.FromBytes(t.Value.Span).ToString(),
                0x87 or 0x89 => t.AsBoolean() ? "TRUE" : "FALSE",
                0xAB => $"{t.Children().Count} items",
                _ => t.AsUnsigned().ToString(CultureInfo.InvariantCulture),
            };
            fields.Add(new(depth + 1, $"{t.Tag:X2}", name, value));
            if (t.Tag == 0xAB)
                foreach (var c in t.Children())
                {
                    var d = MmsData.Decode(c);
                    fields.Add(new(depth + 2, $"{c.Tag:X2}", d.GetType().Name.Replace("Mms", "", StringComparison.Ordinal), Describe(d)));
                }
        }
    }

    private static string Describe(MmsData d) => d switch
    {
        MmsBitString b when b.BitCount == 2 => $"{b} ({b.ToDoublePoint()})",
        MmsBitString b => b.ToString(),
        MmsUtcTime u => u.Value.ToString(),
        MmsBoolean b => b.Value ? "TRUE" : "FALSE",
        MmsFloat f => f.Value.ToString("0.###", CultureInfo.InvariantCulture),
        _ => d.ToString() ?? "",
    };

    private static Iec61850DemoResult Sv(double iRms, double phi, bool vlan, bool id)
    {
        const int spc = 80;
        const double vRms = 20000 / 1.7320508075688772; // 20 kV system, phase-to-neutral
        var asdus = Enumerable.Range(0, spc).Select(i => SampledValuesCodec.Simulate("MU01", (ushort)i, spc, iRms, vRms, phi)).ToList();
        var shown = asdus[10] with { RefreshTime = UtcTime.FromDateTimeOffset(TripTime) };
        var frameRec = new SvFrame { Vlan = vlan ? new VlanTag(4, 0) : null, AppId = 0x4000, Asdus = [shown] };
        byte[] frame = SampledValuesCodec.Encode(frameRec);
        byte[] pdu = SampledValuesCodec.EncodePdu([shown]);
        var back = SampledValuesCodec.Decode(frame);
        bool ok = back.Asdus.Count == 1 && back.Asdus[0].Equals(shown);

        var a = back.Asdus[0];
        var fields = new List<FrameField>
        {
            new(0, "", id ? "Tujuan" : "Destination", back.Destination.ToString()),
            new(0, "", id ? "Sumber" : "Source", back.Source.ToString()),
        };
        if (back.Vlan is VlanTag v) fields.Add(new(0, "8100", "VLAN", $"id {v.VlanId} · priority {v.Priority}"));
        fields.Add(new(0, "88BA", "EtherType", "Sampled Values"));
        fields.Add(new(0, "", "APPID", $"0x{back.AppId:X4}"));
        fields.Add(new(0, "60", "savPdu", $"{pdu.Length} B · noASDU 1"));
        fields.Add(new(1, "80", "svID", a.SvId));
        fields.Add(new(1, "82", "smpCnt", a.SmpCnt.ToString(CultureInfo.InvariantCulture)));
        fields.Add(new(1, "83", "confRev", a.ConfRev.ToString(CultureInfo.InvariantCulture)));
        if (a.RefreshTime is UtcTime rt) fields.Add(new(1, "84", "refrTm", rt.ToString()));
        fields.Add(new(1, "85", "smpSynch", a.SmpSynch.ToString()));
        string[] names = ["Ia", "Ib", "Ic", "In", "Va", "Vb", "Vc", "Vn"];
        for (int k = 0; k < 8; k++)
        {
            double phys = k < 4 ? a.Channels[k].Raw * SvAsdu.CurrentScaleA : a.Channels[k].Raw * SvAsdu.VoltageScaleV;
            fields.Add(new(2, "87", names[k], $"{a.Channels[k].Raw} → {F(phys, "0.0")} {(k < 4 ? "A" : "V")} · {a.Channels[k].Quality}"));
        }

        var cur = asdus.Select(x => x.CurrentsA).ToList();
        string chart = SvgCharts.Line(
        [
            new ChartSeries("Ia", cur.Select(c => c[0]).ToList(), SvgTheme.Var("eln-series-2")),
            new ChartSeries("Ib", cur.Select(c => c[1]).ToList(), SvgTheme.Var("eln-series-3")),
            new ChartSeries("Ic", cur.Select(c => c[2]).ToList(), SvgTheme.Var("eln-series-6")),
            new ChartSeries("In", cur.Select(c => c[3]).ToList(), SvgTheme.Var("eln-muted")) { Dashed = true, StrokeWidth = 1.2 },
        ], new ChartOptions { Width = 640, Height = 240, YLabel = id ? "arus (A)" : "current (A)", XLabel = id ? "sampel (80 per siklus)" : "sample (80 per cycle)" });

        string phasor = SvgCharts.Phasor(
        [
            new PhasorItem("Va", vRms / 1000, 0, PhasorKind.Voltage, SvgTheme.Var("eln-series-2")),
            new PhasorItem("Vb", vRms / 1000, -120, PhasorKind.Voltage, SvgTheme.Var("eln-series-3")),
            new PhasorItem("Vc", vRms / 1000, 120, PhasorKind.Voltage, SvgTheme.Var("eln-series-6")),
            new PhasorItem("Ia", iRms, -phi, PhasorKind.Current, SvgTheme.Var("eln-series-2")),
            new PhasorItem("Ib", iRms, -120 - phi, PhasorKind.Current, SvgTheme.Var("eln-series-3")),
            new PhasorItem("Ic", iRms, 120 - phi, PhasorKind.Current, SvgTheme.Var("eln-series-6")),
        ], null, 320);

        double pf = Math.Cos(phi * Math.PI / 180);
        double pMw = 3 * vRms * iRms * pf / 1e6;
        var kpis = new List<Kpi>
        {
            new(new("Sample rate", "Laju sampel"), "4000", "Hz", new("80 samples/cycle · 50 Hz", "80 sampel/siklus · 50 Hz")),
            new(new("Phase current", "Arus fasa"), F(iRms, "0"), "A rms", new($"φ = {F(phi, "0")}° lagging", $"φ = {F(phi, "0")}° tertinggal")),
            new(new("Active power", "Daya aktif"), F(pMw, "0.00"), "MW", new($"PF {F(pf, "0.000")}", $"PF {F(pf, "0.000")}")),
            new(new("Frame", "Frame"), frame.Length.ToString(CultureInfo.InvariantCulture), "bytes", new("9-2LE · 1 ASDU", "9-2LE · 1 ASDU")),
            new(new("Round trip", "Bolak-balik"), ok ? "MATCH" : "DIFF", null, new("decode(encode(x)) == x", "decode(encode(x)) == x"), ok ? "ok" : "alarm"),
        };
        return new Iec61850DemoResult("sv", frame, EthSegments(vlan, pdu.Length, frame.Length), fields, ok, chart, phasor, SclTree(), kpis);
    }

    /// <summary>Flattened IED model tree from the sample ICD.</summary>
    public static IReadOnlyList<SclNode> SclTree()
    {
        var doc = SclParser.Parse(SampleIcd);
        var nodes = new List<SclNode> { new(0, "SCL", doc.HeaderId ?? "SCL", $"version {doc.Version}{doc.Revision}") };
        foreach (var sn in doc.SubNetworks)
        {
            nodes.Add(new(1, "SubNetwork", sn.Name, sn.Type));
            foreach (var ap in sn.ConnectedAps)
            {
                nodes.Add(new(2, "ConnectedAP", $"{ap.IedName}/{ap.ApName}", ap.Address.TryGetValue("IP", out var ip) ? ip : null));
                foreach (var g in ap.Gse) nodes.Add(new(3, "GSE", g.CbName, $"{g.MacAddress} · APPID {g.AppId:X4} · VLAN {g.VlanId}"));
                foreach (var s in ap.Smv) nodes.Add(new(3, "SMV", s.CbName, $"{s.MacAddress} · APPID {s.AppId:X4}"));
            }
        }
        foreach (var ied in doc.Ieds)
        {
            nodes.Add(new(1, "IED", ied.Name, $"{ied.Type} · {ied.Manufacturer}"));
            foreach (var ld in ied.LDevices)
            {
                nodes.Add(new(2, "LDevice", ied.Name + ld.Inst, ld.Desc));
                foreach (var ln in ld.AllLogicalNodes)
                {
                    nodes.Add(new(3, "LN", ln.Name, ln.Desc ?? ln.LnType));
                    foreach (var ds in ln.DataSets) nodes.Add(new(4, "DataSet", ds.Name, string.Join(", ", ds.Members.Select(m => m.ToReference(ied.Name).ToString()))));
                    foreach (var g in ln.GseControls) nodes.Add(new(4, "GSEControl", g.Name, $"{g.DatSet} · confRev {g.ConfRev}"));
                    foreach (var s in ln.SampledValueControls) nodes.Add(new(4, "SVControl", s.Name, $"{s.SmvId} · {s.SmpRate} smp/cycle"));
                    foreach (var dv in ln.Values) nodes.Add(new(4, "DAI", dv.Path, dv.Value));
                }
            }
        }
        nodes.Add(new(1, "Templates", "DataTypeTemplates", $"{doc.Templates.LNodeTypes.Count} LNodeType · {doc.Templates.DoTypes.Count} DOType · {doc.Templates.DaTypes.Count} DAType"));
        return nodes;
    }

    /// <summary>Bay-controller ICD used by the demo (one IED, control LD with breaker, disconnector and measurement).</summary>
    public const string SampleIcd = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SCL xmlns="http://www.iec.ch/61850/2003/SCL" version="2007" revision="B">
          <Header id="BayController" version="1.0"/>
          <Communication>
            <SubNetwork name="StationBus" type="8-MMS">
              <ConnectedAP iedName="IED1" apName="AP1">
                <Address><P type="IP">192.168.1.10</P><P type="IP-SUBNET">255.255.255.0</P></Address>
                <GSE ldInst="CTRL" cbName="gcbTrip">
                  <Address>
                    <P type="MAC-Address">01-0C-CD-01-00-01</P><P type="APPID">0001</P>
                    <P type="VLAN-ID">00A</P><P type="VLAN-PRIORITY">4</P>
                  </Address>
                  <MinTime unit="s" multiplier="m">2</MinTime>
                  <MaxTime unit="s" multiplier="m">1000</MaxTime>
                </GSE>
                <SMV ldInst="MU" cbName="smv1">
                  <Address><P type="MAC-Address">01-0C-CD-04-00-01</P><P type="APPID">4000</P></Address>
                </SMV>
              </ConnectedAP>
            </SubNetwork>
          </Communication>
          <IED name="IED1" type="BCU" manufacturer="Gravicode" configVersion="1.0">
            <AccessPoint name="AP1">
              <Server>
                <LDevice inst="CTRL" desc="Bay control">
                  <LN0 lnClass="LLN0" inst="" lnType="LLN0_T">
                    <DataSet name="dsTrip">
                      <FCDA ldInst="CTRL" lnClass="XCBR" lnInst="1" doName="Pos" daName="stVal" fc="ST"/>
                      <FCDA ldInst="CTRL" prefix="Q0" lnClass="XSWI" lnInst="1" doName="Pos" fc="ST"/>
                    </DataSet>
                    <GSEControl name="gcbTrip" datSet="dsTrip" appID="IED1_TRIP" confRev="3" type="GOOSE"/>
                    <SampledValueControl name="smv1" datSet="dsTrip" smvID="MU01" smpRate="80" nofASDU="1" confRev="1" multicast="true"/>
                    <DOI name="Mod"><DAI name="ctlModel"><Val>status-only</Val></DAI></DOI>
                  </LN0>
                  <LN lnClass="XCBR" inst="1" lnType="XCBR_T" desc="Feeder breaker">
                    <DOI name="Pos"><DAI name="ctlModel"><Val>direct-with-normal-security</Val></DAI></DOI>
                  </LN>
                  <LN prefix="Q0" lnClass="XSWI" inst="1" lnType="XCBR_T" desc="Line disconnector"/>
                  <LN lnClass="MMXU" inst="1" lnType="MMXU_T" desc="Feeder measurement">
                    <DOI name="TotW"><SDI name="units"><DAI name="multiplier"><Val>M</Val></DAI></SDI></DOI>
                  </LN>
                  <LN lnClass="PTOC" inst="1" lnType="PTOC_T" desc="Overcurrent protection"/>
                </LDevice>
              </Server>
            </AccessPoint>
          </IED>
          <DataTypeTemplates>
            <LNodeType id="LLN0_T" lnClass="LLN0"><DO name="Mod" type="ENC_Mod"/></LNodeType>
            <LNodeType id="XCBR_T" lnClass="XCBR"><DO name="Pos" type="DPC_T"/></LNodeType>
            <LNodeType id="MMXU_T" lnClass="MMXU"><DO name="TotW" type="MV_T"/></LNodeType>
            <LNodeType id="PTOC_T" lnClass="PTOC"><DO name="Mod" type="ENC_Mod"/></LNodeType>
            <DOType id="ENC_Mod" cdc="ENC">
              <DA name="stVal" bType="Enum" type="Beh" fc="ST"/>
              <DA name="ctlModel" bType="Enum" type="ctlModel" fc="CF"><Val>direct-with-normal-security</Val></DA>
            </DOType>
            <DOType id="DPC_T" cdc="DPC">
              <DA name="stVal" bType="Dbpos" fc="ST"/>
              <DA name="q" bType="Quality" fc="ST"/>
              <DA name="t" bType="Timestamp" fc="ST"/>
              <DA name="ctlModel" bType="Enum" type="ctlModel" fc="CF"><Val>status-only</Val></DA>
            </DOType>
            <DOType id="MV_T" cdc="MV">
              <DA name="mag" bType="Struct" type="AV_T" fc="MX"/>
              <DA name="units" bType="Struct" type="Unit_T" fc="CF"/>
            </DOType>
            <DAType id="AV_T"><BDA name="f" bType="FLOAT32"/></DAType>
            <DAType id="Unit_T"><BDA name="SIUnit" bType="Enum" type="SIUnit"><Val>W</Val></BDA><BDA name="multiplier" bType="Enum" type="multiplier"/></DAType>
            <EnumType id="Beh"><EnumVal ord="1">on</EnumVal><EnumVal ord="5">off</EnumVal></EnumType>
          </DataTypeTemplates>
        </SCL>
        """;
}
