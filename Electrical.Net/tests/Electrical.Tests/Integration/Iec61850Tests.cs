using Electrical.Core.Hosting;
using Electrical.IEC61850;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class Iec61850Tests
{
    private const string SampleIcd = """
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
                <LDevice inst="CTRL" desc="Control">
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
                  <LN prefix="Q0" lnClass="XSWI" inst="1" lnType="XCBR_T"/>
                  <LN lnClass="MMXU" inst="1" lnType="MMXU_T">
                    <DOI name="TotW"><SDI name="units"><DAI name="multiplier"><Val>M</Val></DAI></SDI></DOI>
                  </LN>
                </LDevice>
              </Server>
            </AccessPoint>
          </IED>
          <DataTypeTemplates>
            <LNodeType id="LLN0_T" lnClass="LLN0"><DO name="Mod" type="ENC_Mod"/></LNodeType>
            <LNodeType id="XCBR_T" lnClass="XCBR"><DO name="Pos" type="DPC_T"/></LNodeType>
            <LNodeType id="MMXU_T" lnClass="MMXU"><DO name="TotW" type="MV_T"/></LNodeType>
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

    [Fact]
    public void Scl_ParsesIedLogicalNodesAndControlBlocks()
    {
        var doc = SclParser.Parse(SampleIcd);
        Assert.Equal("BayController", doc.HeaderId);
        var ied = Assert.Single(doc.Ieds);
        Assert.Equal("Gravicode", ied.Manufacturer);
        var ld = ied.FindLDevice("IED1CTRL")!;
        Assert.Equal("CTRL", ld.Inst);
        Assert.Equal(new[] { "LLN0", "XCBR1", "Q0XSWI1", "MMXU1" }, ld.AllLogicalNodes.Select(l => l.Name).ToArray());
        Assert.Equal("direct-with-normal-security", ld.FindLogicalNode("XCBR1")!.GetValue("Pos.ctlModel"));
        Assert.Equal("M", ld.FindLogicalNode("MMXU1")!.GetValue("TotW.units.multiplier"));

        var ds = Assert.Single(ld.Ln0.DataSets);
        Assert.Equal(2, ds.Members.Count);
        Assert.Equal("IED1CTRL/XCBR1.Pos.stVal[ST]", ds.Members[0].ToReference("IED1").ToString());
        Assert.Equal("IED1CTRL/Q0XSWI1.Pos[ST]", ds.Members[1].ToReference("IED1").ToString());

        var gcb = Assert.Single(ld.Ln0.GseControls);
        Assert.Equal(("gcbTrip", "dsTrip", 3u), (gcb.Name, gcb.DatSet, gcb.ConfRev));
        var svc = Assert.Single(ld.Ln0.SampledValueControls);
        Assert.Equal(("MU01", 80, 1), (svc.SmvId, svc.SmpRate, svc.NofAsdu));
    }

    [Fact]
    public void Scl_CommunicationAndTemplates()
    {
        var doc = SclParser.Parse(SampleIcd);
        var cap = doc.FindConnectedAp("IED1")!;
        Assert.Equal("192.168.1.10", cap.Address["IP"]);
        var gse = doc.FindGseAddress("IED1", "CTRL", "gcbTrip")!;
        Assert.Equal(MacAddress.Parse("01-0C-CD-01-00-01"), gse.MacAddress);
        Assert.Equal((ushort)1, gse.AppId);
        Assert.Equal((ushort)10, gse.VlanId);
        Assert.Equal((byte)4, gse.VlanPriority);
        Assert.Equal(2.0, gse.MinTimeMs);
        Assert.Equal(1000.0, gse.MaxTimeMs);
        Assert.Equal((ushort)0x4000, Assert.Single(cap.Smv).AppId);

        Assert.Equal("DPC", doc.Templates.DoTypes["DPC_T"].Cdc);
        Assert.Equal("off", doc.Templates.EnumTypes["Beh"].Values[5]);

        var model = doc.DataModel(doc.Ieds[0]).ToList();
        var stVal = model.Single(a => a.Reference.ToString() == "IED1CTRL/XCBR1.Pos.stVal[ST]");
        Assert.Equal("Dbpos", stVal.BType);
        Assert.Contains(model, a => a.Reference.ToString() == "IED1CTRL/MMXU1.TotW.mag.f[MX]" && a.BType == "FLOAT32");
        // Instance value overrides template default; template default used otherwise.
        Assert.Equal("direct-with-normal-security", doc.GetValue("IED1", ObjectReference.Parse("IED1CTRL/XCBR1.Pos.ctlModel")));
        Assert.Equal("status-only", doc.GetValue("IED1", ObjectReference.Parse("IED1CTRL/Q0XSWI1.Pos.ctlModel")));
        Assert.Equal("W", doc.GetValue("IED1", ObjectReference.Parse("IED1CTRL/MMXU1.TotW.units.SIUnit")));
    }

    [Fact]
    public void ObjectReference_ParseFormatAndMms()
    {
        var r = ObjectReference.Parse("IED1CTRL/XCBR1.Pos.stVal[ST]");
        Assert.Equal("IED1CTRL", r.LogicalDevice);
        Assert.Equal("XCBR1", r.LogicalNode);
        Assert.Equal("Pos", r.DataObject);
        Assert.Equal(FunctionalConstraint.ST, r.Fc);
        Assert.Equal("XCBR1$ST$Pos$stVal", r.ToMmsVariable());
        Assert.Equal(r, ObjectReference.FromMms("IED1CTRL", "XCBR1$ST$Pos$stVal"));
        Assert.Equal("IED1CTRL/XCBR1.Pos.stVal[ST]", r.ToString());
        Assert.False(ObjectReference.TryParse("noSlash.Pos", out _));
    }

    [Fact]
    public void LogicalNodeCatalog_DescribesCommonClasses()
    {
        foreach (var cls in new[] { "XCBR", "XSWI", "MMXU", "PTOC", "PDIS", "PDIF", "CSWI", "GGIO", "LLN0" })
            Assert.NotNull(LogicalNodeCatalog.Find(cls));
        Assert.Equal("Circuit breaker", LogicalNodeCatalog.Describe("XCBR"));
        Assert.Equal('P', LogicalNodeCatalog.Find("PDIS")!.Group);
        Assert.Equal("Protection functions", LogicalNodeCatalog.Find("PDIF")!.GroupName);
        Assert.Equal("Switchgear", LogicalNodeCatalog.Describe("XZZZ"));
        Assert.Contains("Pos", LogicalNodeCatalog.Find("XCBR")!.TypicalDataObjects);
    }

    [Theory]
    [InlineData(0L, "020100")]
    [InlineData(127L, "02017F")]
    [InlineData(128L, "02020080")]
    [InlineData(256L, "02020100")]
    [InlineData(-128L, "020180")]
    [InlineData(-129L, "0202FF7F")]
    public void Ber_IntegerKnownVectors(long value, string hex)
    {
        var bytes = new BerWriter().WriteInteger(0x02, value).ToArray();
        Assert.Equal(hex, Convert.ToHexString(bytes));
        var tlv = new BerReader(bytes).Read();
        Assert.Equal(0x02, tlv.Tag);
        Assert.Equal(value, tlv.AsInteger());
    }

    [Fact]
    public void Ber_LengthForms_And_Nesting()
    {
        Assert.Equal("7F", Convert.ToHexString(Ber.EncodeLength(127)));
        Assert.Equal("81C8", Convert.ToHexString(Ber.EncodeLength(200)));
        Assert.Equal("82012C", Convert.ToHexString(Ber.EncodeLength(300)));
        Assert.Equal("0500", Convert.ToHexString(Ber.EncodeUnsigned(1280))); // no sign padding needed
        Assert.Equal("0080", Convert.ToHexString(Ber.EncodeUnsigned(128)));

        var payload = new byte[300];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var enc = new BerWriter().WriteConstructed(0x30, w => w.WriteTlv(0x04, payload).WriteBoolean(0x01, true)).ToArray();
        Assert.Equal("308201330482012C", Convert.ToHexString(enc.AsSpan(0, 8)));
        var seq = new BerReader(enc).Read();
        Assert.True(seq.IsConstructed);
        var kids = seq.Children();
        Assert.Equal(2, kids.Count);
        Assert.True(kids[0].Value.Span.SequenceEqual(payload));
        Assert.True(kids[1].AsBoolean());
        Assert.Throws<FormatException>(() => new BerReader(new byte[] { 0x04, 0x05, 0x01 }).Read());
    }

    private static GoosePdu SamplePdu() => new()
    {
        GoCbRef = "IED1CTRL/LLN0$GO$gcbTrip",
        DatSet = "IED1CTRL/LLN0$dsTrip",
        GoId = "IED1_TRIP",
        TimeAllowedToLiveMs = 4,
        Timestamp = UtcTime.FromDateTimeOffset(new DateTimeOffset(2026, 11, 14, 8, 30, 0, 500, TimeSpan.Zero)),
        StNum = 7,
        SqNum = 0,
        ConfRev = 3,
        Data =
        [
            new MmsBoolean(true),
            new MmsInteger(-42),
            new MmsUnsigned(300),
            new MmsFloat(49.98f),
            MmsBitString.FromDoublePoint(DoublePoint.On),
            new MmsBitString([0x00, 0x00], 13), // quality: good
            new MmsUtcTime(new UtcTime(1_700_000_000, 0x800000)),
            new MmsVisibleString("Q0"),
            new MmsStructure([new MmsBoolean(false), new MmsFloat(1.5f)]),
        ],
    };

    [Fact]
    public void Goose_FrameRoundTrip_WithoutVlan()
    {
        var frame = new GooseFrame { Destination = MacAddress.Goose(1), AppId = 0x0001, Pdu = SamplePdu() };
        var bytes = GooseCodec.Encode(frame);
        Assert.Equal("010CCD010001", Convert.ToHexString(bytes, 0, 6));
        Assert.Equal(0x88, bytes[12]); Assert.Equal(0xB8, bytes[13]);
        Assert.Equal(0x00, bytes[14]); Assert.Equal(0x01, bytes[15]);           // APPID
        int len = bytes[16] << 8 | bytes[17];
        Assert.Equal(bytes.Length - 14, len);                                   // no padding needed for this size
        Assert.Equal(0x61, bytes[22]);                                          // goosePdu tag

        var back = GooseCodec.Decode(bytes);
        Assert.Null(back.Vlan);
        Assert.Equal(frame.Destination, back.Destination);
        Assert.Equal(frame.Pdu, back.Pdu);
        Assert.Equal(DoublePoint.On, ((MmsBitString)back.Pdu.Data[4]).ToDoublePoint());
        Assert.Equal(49.98f, ((MmsFloat)back.Pdu.Data[3]).Value);
        Assert.Equal(frame.Pdu.Timestamp.ToDateTimeOffset(), back.Pdu.Timestamp.ToDateTimeOffset());
        Assert.Equal(500, back.Pdu.Timestamp.ToDateTimeOffset().Millisecond);
    }

    [Fact]
    public void Goose_FrameRoundTrip_WithVlan()
    {
        var small = new GoosePdu { GoCbRef = "A/LLN0$GO$g", DatSet = "A/LLN0$ds", Data = [new MmsBoolean(false)] };
        var frame = new GooseFrame { Vlan = new VlanTag(4, 10), AppId = 0x3FFF, Reserved1 = 0x8000, Pdu = small };
        var bytes = GooseCodec.Encode(frame);
        Assert.Equal(small, GooseCodec.Decode([.. bytes, .. new byte[10]]).Pdu); // trailing padding ignored via Length field
        Assert.Equal("8100800A88B8", Convert.ToHexString(bytes, 12, 6));   // TPID, PCP 4 / VID 10, EtherType
        var back = GooseCodec.Decode(bytes);
        Assert.Equal(new VlanTag(4, 10), back.Vlan);
        Assert.Equal((ushort)0x3FFF, back.AppId);
        Assert.Equal((ushort)0x8000, back.Reserved1);
        Assert.Equal(small, back.Pdu);
        Assert.Throws<FormatException>(() => SampledValuesCodec.Decode(bytes));
    }

    [Fact]
    public void SampledValues_92LE_RoundTrip()
    {
        var asdus = Enumerable.Range(0, 8).Select(i =>
            SampledValuesCodec.Simulate("MU01", (ushort)i, 80, iRmsA: 400, vRmsV: 11547, phiDeg: 30) with { RefreshTime = new UtcTime(1_700_000_000, 0x123456) })
            .ToArray();
        var frame = new SvFrame { Vlan = new VlanTag(4, 0), AppId = 0x4000, Asdus = asdus };
        var bytes = SampledValuesCodec.Encode(frame);
        Assert.Equal("88BA", Convert.ToHexString(bytes, 16, 2));
        var back = SampledValuesCodec.Decode(bytes);
        Assert.Equal(8, back.Asdus.Count);
        for (int i = 0; i < 8; i++) Assert.Equal(asdus[i], back.Asdus[i]);
        // Sample 0: Va = Vpeak, Ia = Ipeak·cos(-30°); neutrals ≈ 0 and flagged derived.
        var a0 = back.Asdus[0];
        Assert.Equal(11547 * Math.Sqrt(2), a0.VoltagesV[0], 0.01);
        Assert.Equal(400 * Math.Sqrt(2) * Math.Cos(Math.PI / 6), a0.CurrentsA[0], 0.001);
        Assert.InRange(Math.Abs(a0.CurrentsA[3]), 0, 0.005);
        Assert.Equal(SvQuality.Derived, a0.Channels[3].Quality);
        Assert.Equal(SvQuality.Good, a0.Channels[0].Quality);
        // Exact byte layout of the 9-2LE sample: Ia raw value as INT32 big-endian.
        var single = SampledValuesCodec.EncodePdu([SvAsdu.FromPhysical("X", 1, [1.234, 0, 0, 0], [0, 0, 0, 0])]);
        int idx = Array.IndexOf(single, (byte)0x87);
        Assert.Equal("8740000004D200000000", Convert.ToHexString(single, idx, 10));
    }

    [Fact]
    public void GooseRetransmission_ScheduleAndState()
    {
        var sched = new GooseRetransmission(MinTimeMs: 2, MaxTimeMs: 1000);
        Assert.Equal(new double[] { 2, 4, 8, 16, 32, 64, 128, 256, 512, 1000, 1000 }, sched.Intervals(11));
        Assert.Equal(new double[] { 0, 2, 6, 14 }, sched.TransmissionTimes(4));
        Assert.Equal(4u, sched.TimeAllowedToLive(0));
        Assert.Equal(2000u, sched.TimeAllowedToLive(20));

        var st = new GoosePublisherState(sched);
        st.OnStateChange(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal((2u, 0u), (st.StNum, st.SqNum));
        var (interval, tal) = st.NextRetransmission();
        Assert.Equal((2.0, 8u), (interval, tal));
        st.NextRetransmission();
        var pdu = st.CreatePdu("A/LLN0$GO$g", "A/LLN0$ds", [new MmsBoolean(true)]);
        Assert.Equal((2u, 2u), (pdu.StNum, pdu.SqNum));
        st.OnStateChange(DateTimeOffset.UnixEpoch);
        Assert.Equal((3u, 0u), (st.StNum, st.SqNum));
    }

    [Fact]
    public void DependencyInjection_RegistersService()
    {
        using var sp = new ServiceCollection().AddElectrical(b => b.AddIec61850().AddIec61850()).BuildServiceProvider();
        var svc = sp.GetRequiredService<IIec61850Service>();
        Assert.Single(svc.ParseScl(SampleIcd).Ieds);
        Assert.Contains("IEC61850", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
    }
}
