using System.Buffers.Binary;

namespace Electrical.IEC61850;

/// <summary>IEC 61850-7-3 quality word as transmitted in 9-2LE (32 bits per channel).</summary>
[Flags]
public enum SvQuality : uint
{
    Good = 0,
    /// <summary>Validity = invalid (01).</summary>
    Invalid = 0x1,
    /// <summary>Validity = questionable (11).</summary>
    Questionable = 0x3,
    Overflow = 0x4,
    OutOfRange = 0x8,
    BadReference = 0x10,
    Oscillatory = 0x20,
    Failure = 0x40,
    OldData = 0x80,
    Inconsistent = 0x100,
    Inaccurate = 0x200,
    /// <summary>Source = substituted.</summary>
    Substituted = 0x400,
    Test = 0x800,
    OperatorBlocked = 0x1000,
    /// <summary>Derived (e.g. neutral computed from phases).</summary>
    Derived = 0x2000,
}

/// <summary>One 9-2LE channel: raw INT32 value and quality.</summary>
public readonly record struct SvChannel(int Raw, SvQuality Quality = SvQuality.Good);

/// <summary>Sample synchronisation source.</summary>
public enum SmpSynch : byte { None = 0, Local = 1, Global = 2 }

/// <summary>
/// One ASDU of the IEC 61850-9-2 LE profile: dataset of 4 currents (Ia, Ib, Ic, In; scaling 1 mA) and 4 voltages
/// (Va, Vb, Vc, Vn; scaling 10 mV), each with quality.
/// </summary>
public sealed record SvAsdu
{
    /// <summary>Current LSB [A] (9-2LE: 1 mA).</summary>
    public const double CurrentScaleA = 0.001;
    /// <summary>Voltage LSB [V] (9-2LE: 10 mV).</summary>
    public const double VoltageScaleV = 0.01;

    public required string SvId { get; init; }
    public string? DatSet { get; init; }
    /// <summary>Sample counter (wraps at the sample rate × 1 s, e.g. 0…3999 at 80 samples/cycle, 50 Hz).</summary>
    public ushort SmpCnt { get; init; }
    public uint ConfRev { get; init; } = 1;
    public UtcTime? RefreshTime { get; init; }
    public SmpSynch SmpSynch { get; init; } = SmpSynch.Global;
    /// <summary>Optional sample rate (samples per nominal period).</summary>
    public ushort? SmpRate { get; init; }
    /// <summary>Exactly 8 channels: Ia, Ib, Ic, In, Va, Vb, Vc, Vn.</summary>
    public required SvChannel[] Channels { get; init; }

    /// <summary>Currents [A] (Ia, Ib, Ic, In).</summary>
    public double[] CurrentsA => [.. Channels.Take(4).Select(c => c.Raw * CurrentScaleA)];
    /// <summary>Voltages [V] (Va, Vb, Vc, Vn).</summary>
    public double[] VoltagesV => [.. Channels.Skip(4).Take(4).Select(c => c.Raw * VoltageScaleV)];

    /// <summary>Builds an ASDU from instantaneous physical values: 4 currents [A] and 4 voltages [V].</summary>
    public static SvAsdu FromPhysical(string svId, ushort smpCnt, ReadOnlySpan<double> currentsA, ReadOnlySpan<double> voltagesV,
        SvQuality quality = SvQuality.Good, uint confRev = 1)
    {
        if (currentsA.Length != 4 || voltagesV.Length != 4) throw new ArgumentException("9-2LE needs 4 currents and 4 voltages.");
        var ch = new SvChannel[8];
        for (int i = 0; i < 4; i++)
        {
            ch[i] = new SvChannel(checked((int)Math.Round(currentsA[i] / CurrentScaleA)), quality);
            ch[4 + i] = new SvChannel(checked((int)Math.Round(voltagesV[i] / VoltageScaleV)), quality);
        }
        return new SvAsdu { SvId = svId, SmpCnt = smpCnt, ConfRev = confRev, Channels = ch };
    }

    public bool Equals(SvAsdu? o) => o is not null && SvId == o.SvId && DatSet == o.DatSet && SmpCnt == o.SmpCnt && ConfRev == o.ConfRev &&
        RefreshTime == o.RefreshTime && SmpSynch == o.SmpSynch && SmpRate == o.SmpRate && Channels.AsSpan().SequenceEqual(o.Channels);

    public override int GetHashCode() => SmpCnt;
}

/// <summary>Sampled-values Ethernet frame (EtherType 0x88BA) with one or more ASDUs.</summary>
public sealed record SvFrame
{
    public MacAddress Destination { get; init; } = MacAddress.SampledValues(1);
    public MacAddress Source { get; init; } = MacAddress.Parse("00-1A-B6-00-00-02");
    public VlanTag? Vlan { get; init; }
    /// <summary>Application id (SV range 0x4000–0x7FFF).</summary>
    public ushort AppId { get; init; } = 0x4000;
    public ushort Reserved1 { get; init; }
    public ushort Reserved2 { get; init; }
    public required IReadOnlyList<SvAsdu> Asdus { get; init; }
}

/// <summary>IEC 61850-9-2 (LE) sampled-values encoder/decoder.</summary>
public static class SampledValuesCodec
{
    private const byte TagSavPdu = 0x60, TagAsdu = 0x30;

    /// <summary>BER-encodes the savPdu (tag 0x60).</summary>
    public static byte[] EncodePdu(IReadOnlyList<SvAsdu> asdus)
    {
        var w = new BerWriter();
        w.WriteConstructed(TagSavPdu, x =>
        {
            x.WriteUnsigned(0x80, (ulong)asdus.Count);
            x.WriteConstructed(0xA2, seq =>
            {
                foreach (var a in asdus) seq.WriteConstructed(TagAsdu, e => EncodeAsdu(e, a));
            });
        });
        return w.ToArray();
    }

    private static void EncodeAsdu(BerWriter e, SvAsdu a)
    {
        if (a.Channels.Length != 8) throw new ArgumentException("9-2LE ASDU needs 8 channels.");
        e.WriteVisibleString(0x80, a.SvId);
        if (a.DatSet is not null) e.WriteVisibleString(0x81, a.DatSet);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(b, a.SmpCnt);
        e.WriteTlv(0x82, b[..2]);
        BinaryPrimitives.WriteUInt32BigEndian(b, a.ConfRev);
        e.WriteTlv(0x83, b);
        if (a.RefreshTime is UtcTime rt) e.WriteTlv(0x84, rt.ToBytes());
        e.WriteTlv(0x85, [(byte)a.SmpSynch]);
        if (a.SmpRate is ushort sr) { BinaryPrimitives.WriteUInt16BigEndian(b, sr); e.WriteTlv(0x86, b[..2]); }
        var sample = new byte[64];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(sample.AsSpan(i * 8), a.Channels[i].Raw);
            BinaryPrimitives.WriteUInt32BigEndian(sample.AsSpan(i * 8 + 4), (uint)a.Channels[i].Quality);
        }
        e.WriteTlv(0x87, sample);
    }

    /// <summary>Decodes a savPdu (tag 0x60).</summary>
    public static IReadOnlyList<SvAsdu> DecodePdu(ReadOnlyMemory<byte> pdu)
    {
        var root = new BerReader(pdu).Read();
        if (root.Tag != TagSavPdu) throw new FormatException($"Not a savPdu (tag 0x{root.Tag:X2}).");
        int expected = -1;
        var list = new List<SvAsdu>();
        foreach (var e in root.Children())
        {
            if (e.Tag == 0x80) expected = (int)e.AsUnsigned();
            else if (e.Tag == 0xA2)
                foreach (var asdu in e.Children())
                    if (asdu.Tag == TagAsdu) list.Add(DecodeAsdu(asdu));
        }
        if (expected >= 0 && expected != list.Count) throw new FormatException($"noASDU {expected} ≠ {list.Count} ASDUs.");
        return list;
    }

    private static SvAsdu DecodeAsdu(BerTlv asdu)
    {
        string? id = null, ds = null;
        ushort cnt = 0; uint conf = 0; UtcTime? rt = null; var sync = SmpSynch.None; ushort? rate = null;
        SvChannel[]? ch = null;
        foreach (var f in asdu.Children())
        {
            var v = f.Value.Span;
            switch (f.Tag)
            {
                case 0x80: id = f.AsString(); break;
                case 0x81: ds = f.AsString(); break;
                case 0x82: cnt = (ushort)Ber.DecodeUnsigned(v); break;
                case 0x83: conf = (uint)Ber.DecodeUnsigned(v); break;
                case 0x84: rt = UtcTime.FromBytes(v); break;
                case 0x85: sync = (SmpSynch)v[0]; break;
                case 0x86: rate = (ushort)Ber.DecodeUnsigned(v); break;
                case 0x87:
                    if (v.Length != 64) throw new FormatException($"9-2LE sample must be 64 octets, got {v.Length}.");
                    ch = new SvChannel[8];
                    for (int i = 0; i < 8; i++)
                        ch[i] = new SvChannel(BinaryPrimitives.ReadInt32BigEndian(v[(i * 8)..]), (SvQuality)BinaryPrimitives.ReadUInt32BigEndian(v[(i * 8 + 4)..]));
                    break;
            }
        }
        if (id is null || ch is null) throw new FormatException("ASDU lacks svID or sample.");
        return new SvAsdu { SvId = id, DatSet = ds, SmpCnt = cnt, ConfRev = conf, RefreshTime = rt, SmpSynch = sync, SmpRate = rate, Channels = ch };
    }

    public static byte[] Encode(SvFrame f, bool pad = true) =>
        EthernetFrame.Build(f.Destination, f.Source, f.Vlan, EthernetFrame.EtherTypeSampledValues, f.AppId, f.Reserved1, f.Reserved2, EncodePdu(f.Asdus), pad);

    public static SvFrame Decode(byte[] frame)
    {
        var h = EthernetFrame.Parse(frame);
        if (h.EtherType != EthernetFrame.EtherTypeSampledValues) throw new FormatException($"EtherType 0x{h.EtherType:X4} is not SV.");
        return new SvFrame
        {
            Destination = h.Dst, Source = h.Src, Vlan = h.Vlan, AppId = h.AppId, Reserved1 = h.Reserved1, Reserved2 = h.Reserved2,
            Asdus = DecodePdu(frame.AsMemory(h.PduOffset, h.PduLength)),
        };
    }

    /// <summary>
    /// Generates one ASDU of a balanced three-phase waveform (deterministic merging-unit simulator):
    /// peak values from RMS phase current [A] and phase-to-neutral voltage [V], phase angle of current lagging voltage by <paramref name="phiDeg"/>.
    /// </summary>
    public static SvAsdu Simulate(string svId, ushort smpCnt, int samplesPerCycle, double iRmsA, double vRmsV, double phiDeg = 0)
    {
        double wt = 2 * Math.PI * (smpCnt % samplesPerCycle) / samplesPerCycle;
        double phi = phiDeg * Math.PI / 180, ip = iRmsA * Math.Sqrt(2), vp = vRmsV * Math.Sqrt(2);
        double[] shift = [0, -2 * Math.PI / 3, 2 * Math.PI / 3];
        Span<double> i = stackalloc double[4], v = stackalloc double[4];
        for (int k = 0; k < 3; k++) { v[k] = vp * Math.Cos(wt + shift[k]); i[k] = ip * Math.Cos(wt + shift[k] - phi); }
        i[3] = i[0] + i[1] + i[2];
        v[3] = v[0] + v[1] + v[2];
        var a = SvAsdu.FromPhysical(svId, smpCnt, i, v);
        a.Channels[3] = a.Channels[3] with { Quality = SvQuality.Derived };
        a.Channels[7] = a.Channels[7] with { Quality = SvQuality.Derived };
        return a;
    }
}
