namespace Electrical.IEC61850;

/// <summary>
/// GOOSE PDU (IEC 61850-8-1 §A.3, <c>IECGoosePdu</c> [APPLICATION 1]). Times in milliseconds.
/// </summary>
public sealed record GoosePdu
{
    /// <summary>GoCB reference, e.g. "IED1CTRL/LLN0$GO$gcbTrip".</summary>
    public required string GoCbRef { get; init; }
    /// <summary>Time allowed to live [ms] before subscribers declare the message stale (typically 2× the next retransmission interval).</summary>
    public uint TimeAllowedToLiveMs { get; init; } = 2000;
    /// <summary>Dataset reference, e.g. "IED1CTRL/LLN0$dsTrip".</summary>
    public required string DatSet { get; init; }
    public string? GoId { get; init; }
    /// <summary>Time of the last state change (stNum increment).</summary>
    public UtcTime Timestamp { get; init; }
    /// <summary>State number, incremented on every dataset change.</summary>
    public uint StNum { get; init; } = 1;
    /// <summary>Sequence number, incremented on every retransmission, reset to 0 on state change.</summary>
    public uint SqNum { get; init; }
    /// <summary>Simulation / test flag.</summary>
    public bool Simulation { get; init; }
    public uint ConfRev { get; init; } = 1;
    /// <summary>Needs commissioning.</summary>
    public bool NdsCom { get; init; }
    /// <summary>Dataset member values in dataset order.</summary>
    public IReadOnlyList<MmsData> Data { get; init; } = [];

    public bool Equals(GoosePdu? o) => o is not null && GoCbRef == o.GoCbRef && TimeAllowedToLiveMs == o.TimeAllowedToLiveMs && DatSet == o.DatSet &&
        GoId == o.GoId && Timestamp == o.Timestamp && StNum == o.StNum && SqNum == o.SqNum && Simulation == o.Simulation && ConfRev == o.ConfRev &&
        NdsCom == o.NdsCom && Data.SequenceEqual(o.Data);

    public override int GetHashCode() => (int)(StNum * 31 + SqNum);
}

/// <summary>A GOOSE Ethernet frame: link-layer header plus <see cref="GoosePdu"/>.</summary>
public sealed record GooseFrame
{
    /// <summary>Destination multicast address (01-0C-CD-01-xx-xx).</summary>
    public MacAddress Destination { get; init; } = MacAddress.Goose(1);
    public MacAddress Source { get; init; } = MacAddress.Parse("00-1A-B6-00-00-01");
    /// <summary>Optional 802.1Q tag (GOOSE default priority 4).</summary>
    public VlanTag? Vlan { get; init; }
    /// <summary>Application id (GOOSE range 0x0000–0x3FFF).</summary>
    public ushort AppId { get; init; } = 0x0001;
    /// <summary>Reserved 1 (bit 15 = simulation in Edition 2).</summary>
    public ushort Reserved1 { get; init; }
    public ushort Reserved2 { get; init; }
    public required GoosePdu Pdu { get; init; }
}

/// <summary>GOOSE encoder/decoder.</summary>
public static class GooseCodec
{
    private const byte TagPdu = 0x61;

    /// <summary>BER-encodes the GOOSE PDU (starting with tag 0x61).</summary>
    public static byte[] EncodePdu(GoosePdu p)
    {
        var w = new BerWriter();
        w.WriteConstructed(TagPdu, x =>
        {
            x.WriteVisibleString(0x80, p.GoCbRef);
            x.WriteUnsigned(0x81, p.TimeAllowedToLiveMs);
            x.WriteVisibleString(0x82, p.DatSet);
            if (p.GoId is not null) x.WriteVisibleString(0x83, p.GoId);
            x.WriteTlv(0x84, p.Timestamp.ToBytes());
            x.WriteUnsigned(0x85, p.StNum);
            x.WriteUnsigned(0x86, p.SqNum);
            x.WriteBoolean(0x87, p.Simulation);
            x.WriteUnsigned(0x88, p.ConfRev);
            x.WriteBoolean(0x89, p.NdsCom);
            x.WriteUnsigned(0x8A, (ulong)p.Data.Count);
            x.WriteConstructed(0xAB, d => { foreach (var v in p.Data) v.Encode(d); });
        });
        return w.ToArray();
    }

    /// <summary>Decodes a GOOSE PDU (tag 0x61).</summary>
    public static GoosePdu DecodePdu(ReadOnlyMemory<byte> pdu)
    {
        var root = new BerReader(pdu).Read();
        if (root.Tag != TagPdu) throw new FormatException($"Not a GOOSE PDU (tag 0x{root.Tag:X2}).");
        string? gocb = null, ds = null, goId = null;
        uint tal = 0, st = 0, sq = 0, conf = 0;
        bool sim = false, nds = false;
        UtcTime t = default;
        IReadOnlyList<MmsData> data = [];
        long numEntries = -1;
        foreach (var e in root.Children())
        {
            switch (e.Tag)
            {
                case 0x80: gocb = e.AsString(); break;
                case 0x81: tal = (uint)e.AsUnsigned(); break;
                case 0x82: ds = e.AsString(); break;
                case 0x83: goId = e.AsString(); break;
                case 0x84: t = UtcTime.FromBytes(e.Value.Span); break;
                case 0x85: st = (uint)e.AsUnsigned(); break;
                case 0x86: sq = (uint)e.AsUnsigned(); break;
                case 0x87: sim = e.AsBoolean(); break;
                case 0x88: conf = (uint)e.AsUnsigned(); break;
                case 0x89: nds = e.AsBoolean(); break;
                case 0x8A: numEntries = (long)e.AsUnsigned(); break;
                case 0xAB: data = e.Children().Select(MmsData.Decode).ToArray(); break;
            }
        }
        if (gocb is null || ds is null) throw new FormatException("GOOSE PDU lacks gocbRef or datSet.");
        if (numEntries >= 0 && numEntries != data.Count) throw new FormatException($"numDatSetEntries {numEntries} ≠ {data.Count} values.");
        return new GoosePdu
        {
            GoCbRef = gocb, TimeAllowedToLiveMs = tal, DatSet = ds, GoId = goId, Timestamp = t, StNum = st, SqNum = sq, Simulation = sim,
            ConfRev = conf, NdsCom = nds, Data = data,
        };
    }

    /// <summary>Encodes the complete Ethernet frame (without FCS), padded to 60 bytes when <paramref name="pad"/> is true.</summary>
    public static byte[] Encode(GooseFrame f, bool pad = true) =>
        EthernetFrame.Build(f.Destination, f.Source, f.Vlan, EthernetFrame.EtherTypeGoose, f.AppId, f.Reserved1, f.Reserved2, EncodePdu(f.Pdu), pad);

    /// <summary>Decodes an Ethernet frame carrying GOOSE (with or without 802.1Q tag).</summary>
    public static GooseFrame Decode(byte[] frame)
    {
        var h = EthernetFrame.Parse(frame);
        if (h.EtherType != EthernetFrame.EtherTypeGoose) throw new FormatException($"EtherType 0x{h.EtherType:X4} is not GOOSE.");
        return new GooseFrame
        {
            Destination = h.Dst, Source = h.Src, Vlan = h.Vlan, AppId = h.AppId, Reserved1 = h.Reserved1, Reserved2 = h.Reserved2,
            Pdu = DecodePdu(frame.AsMemory(h.PduOffset, h.PduLength)),
        };
    }
}

/// <summary>
/// GOOSE retransmission schedule (IEC 61850-8-1 §18.1): after a state change the message is repeated after T1 (minimum time),
/// then at intervals growing geometrically until the heartbeat T0 (maximum time) is reached. All times in milliseconds.
/// </summary>
public sealed record GooseRetransmission(double MinTimeMs = 2, double MaxTimeMs = 1000, double Multiplier = 2)
{
    /// <summary>Interval [ms] before retransmission number <paramref name="sqNum"/> (1-based; sqNum 1 = first repeat after a change).</summary>
    public double IntervalAfter(uint sqNum)
    {
        if (sqNum <= 1) return MinTimeMs;
        double v = MinTimeMs * Math.Pow(Multiplier, sqNum - 1);
        return Math.Min(v, MaxTimeMs);
    }

    /// <summary>The first <paramref name="count"/> intervals [ms] after a state change, e.g. 2, 4, 8, … 1000, 1000.</summary>
    public IReadOnlyList<double> Intervals(int count) => Enumerable.Range(1, count).Select(i => IntervalAfter((uint)i)).ToArray();

    /// <summary>Transmission instants [ms after the event] including the spontaneous one at t = 0.</summary>
    public IReadOnlyList<double> TransmissionTimes(int count)
    {
        var t = new double[count];
        for (int i = 1; i < count; i++) t[i] = t[i - 1] + IntervalAfter((uint)i);
        return t;
    }

    /// <summary>Time allowed to live [ms] for a message sent with sequence number <paramref name="sqNum"/>: twice the next interval.</summary>
    public uint TimeAllowedToLive(uint sqNum) => (uint)Math.Ceiling(2 * IntervalAfter(sqNum + 1));
}

/// <summary>Publisher-side stNum/sqNum state machine with wrap-around per IEC 61850-8-1 (wraps to 1).</summary>
public sealed class GoosePublisherState(GooseRetransmission? schedule = null)
{
    public GooseRetransmission Schedule { get; } = schedule ?? new();
    public uint StNum { get; private set; } = 1;
    public uint SqNum { get; private set; }
    /// <summary>Timestamp of the last state change.</summary>
    public UtcTime LastChange { get; private set; }

    /// <summary>Registers a dataset change: stNum++, sqNum = 0. Returns the TAL [ms] for the spontaneous message.</summary>
    public uint OnStateChange(DateTimeOffset time)
    {
        StNum = StNum == uint.MaxValue ? 1 : StNum + 1;
        SqNum = 0;
        LastChange = UtcTime.FromDateTimeOffset(time);
        return Schedule.TimeAllowedToLive(0);
    }

    /// <summary>Advances to the next retransmission; returns (interval to wait before it [ms], TAL [ms]).</summary>
    public (double IntervalMs, uint TimeAllowedToLiveMs) NextRetransmission()
    {
        SqNum = SqNum == uint.MaxValue ? 1 : SqNum + 1;
        return (Schedule.IntervalAfter(SqNum), Schedule.TimeAllowedToLive(SqNum));
    }

    /// <summary>Builds the PDU for the current state.</summary>
    public GoosePdu CreatePdu(string goCbRef, string datSet, IReadOnlyList<MmsData> data, string? goId = null, uint confRev = 1) => new()
    {
        GoCbRef = goCbRef, DatSet = datSet, GoId = goId, Data = data, StNum = StNum, SqNum = SqNum, ConfRev = confRev, Timestamp = LastChange,
        TimeAllowedToLiveMs = Schedule.TimeAllowedToLive(SqNum),
    };
}
