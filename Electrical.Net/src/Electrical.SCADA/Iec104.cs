using System.Buffers.Binary;

namespace Electrical.SCADA.Iec104;

/// <summary>APCI frame format (IEC 60870-5-104 §5).</summary>
public enum ApciFormat
{
    /// <summary>Numbered information transfer (carries an ASDU).</summary>
    I,
    /// <summary>Numbered supervisory (acknowledges received I-frames).</summary>
    S,
    /// <summary>Unnumbered control (STARTDT/STOPDT/TESTFR).</summary>
    U,
}

/// <summary>U-frame functions (first control octet values).</summary>
public enum UFunction : byte
{
    StartDtAct = 0x07,
    StartDtCon = 0x0B,
    StopDtAct = 0x13,
    StopDtCon = 0x23,
    TestFrAct = 0x43,
    TestFrCon = 0x83,
}

/// <summary>ASDU type identifiers supported by the codec (IEC 60870-5-101/104 companion standard).</summary>
public enum TypeId : byte
{
    /// <summary>Single-point information (SIQ).</summary>
    M_SP_NA_1 = 1,
    /// <summary>Measured value, short floating point (IEEE 754 + QDS).</summary>
    M_ME_NC_1 = 13,
    /// <summary>Single-point information with CP56Time2a time tag.</summary>
    M_SP_TB_1 = 30,
    /// <summary>Measured value, short floating point, with CP56Time2a time tag.</summary>
    M_ME_TF_1 = 36,
    /// <summary>Single command (SCO).</summary>
    C_SC_NA_1 = 45,
    /// <summary>Single command with CP56Time2a time tag.</summary>
    C_SC_TA_1 = 58,
}

/// <summary>Cause of transmission (6 bits).</summary>
public enum CauseOfTransmission : byte
{
    Periodic = 1,
    Background = 2,
    Spontaneous = 3,
    Initialized = 4,
    Request = 5,
    Activation = 6,
    ActivationConfirmation = 7,
    Deactivation = 8,
    DeactivationConfirmation = 9,
    ActivationTermination = 10,
    InterrogatedByStation = 20,
    UnknownTypeId = 44,
    UnknownCause = 45,
    UnknownCommonAddress = 46,
    UnknownObjectAddress = 47,
}

/// <summary>Quality descriptor bits (QDS / SIQ upper bits).</summary>
[Flags]
public enum Iec104Quality : byte
{
    Good = 0,
    /// <summary>Overflow (QDS only).</summary>
    Overflow = 0x01,
    Blocked = 0x10,
    Substituted = 0x20,
    NotTopical = 0x40,
    Invalid = 0x80,
}

/// <summary>
/// Information object. <see cref="Value"/> holds the measured value (M_ME_*) or the state 0/1 (M_SP_*, C_SC_*).
/// For commands, <see cref="Select"/> is the S/E bit and <see cref="Qualifier"/> the QU field (0–31).
/// </summary>
/// <param name="Address">Information object address (24 bit).</param>
public sealed record InformationObject(uint Address, double Value, Iec104Quality Quality = Iec104Quality.Good, DateTimeOffset? Time = null,
    bool Select = false, byte Qualifier = 0)
{
    public bool State => Value != 0;
}

/// <summary>Application service data unit (2-octet common address, 1-octet originator address, 3-octet IOA).</summary>
public sealed record Asdu(TypeId Type, CauseOfTransmission Cause, ushort CommonAddress, IReadOnlyList<InformationObject> Objects,
    bool Negative = false, bool Test = false, byte OriginatorAddress = 0)
{
    public static bool HasTime(TypeId t) => t is TypeId.M_SP_TB_1 or TypeId.M_ME_TF_1 or TypeId.C_SC_TA_1;

    /// <summary>Size of one information element (without IOA) [bytes].</summary>
    public static int ElementSize(TypeId t) => t switch
    {
        TypeId.M_SP_NA_1 or TypeId.C_SC_NA_1 => 1,
        TypeId.M_SP_TB_1 or TypeId.C_SC_TA_1 => 1 + 7,
        TypeId.M_ME_NC_1 => 5,
        TypeId.M_ME_TF_1 => 5 + 7,
        _ => throw new NotSupportedException($"Type id {(byte)t} not supported."),
    };
}

/// <summary>Application protocol data unit: APCI (start 0x68, length, 4 control octets) plus optional ASDU.</summary>
/// <param name="SendSequence">N(S) for I-frames (15 bit).</param>
/// <param name="ReceiveSequence">N(R) for I- and S-frames (15 bit).</param>
public sealed record Apdu(ApciFormat Format, ushort SendSequence = 0, ushort ReceiveSequence = 0, UFunction? Function = null, Asdu? Asdu = null)
{
    public static Apdu U(UFunction f) => new(ApciFormat.U, Function: f);
    public static Apdu S(ushort receiveSequence) => new(ApciFormat.S, 0, receiveSequence);
    public static Apdu I(ushort sendSequence, ushort receiveSequence, Asdu asdu) => new(ApciFormat.I, sendSequence, receiveSequence, null, asdu);
}

/// <summary>CP56Time2a seven-octet binary time (ms, minute, hour, day/day-of-week, month, year 0–99). Interpreted as UTC.</summary>
public static class Cp56Time2a
{
    public const int Length = 7;

    /// <summary>Encodes a time; <paramref name="invalid"/> sets the IV bit, <paramref name="summerTime"/> the SU bit.</summary>
    public static void Encode(DateTimeOffset time, Span<byte> dest, bool invalid = false, bool summerTime = false)
    {
        var t = time.UtcDateTime;
        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)(t.Second * 1000 + t.Millisecond));
        dest[2] = (byte)(t.Minute | (invalid ? 0x80 : 0));
        dest[3] = (byte)(t.Hour | (summerTime ? 0x80 : 0));
        int dow = t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek; // 1 = Monday … 7 = Sunday
        dest[4] = (byte)(t.Day | (dow << 5));
        dest[5] = (byte)t.Month;
        dest[6] = (byte)(t.Year % 100);
    }

    /// <summary>Decodes a time (years 00–99 map to 2000–2099).</summary>
    public static DateTimeOffset Decode(ReadOnlySpan<byte> src, out bool invalid)
    {
        int ms = BinaryPrimitives.ReadUInt16LittleEndian(src);
        invalid = (src[2] & 0x80) != 0;
        var dt = new DateTime(2000 + (src[6] & 0x7F), src[5] & 0x0F, src[4] & 0x1F, src[3] & 0x1F, src[2] & 0x3F, 0, DateTimeKind.Utc);
        return new DateTimeOffset(dt.AddMilliseconds(ms));
    }

    public static DateTimeOffset Decode(ReadOnlySpan<byte> src) => Decode(src, out _);
}

/// <summary>Encoder/decoder for IEC 60870-5-104 APDUs (codec only; no link-layer state machine).</summary>
public static class Iec104Codec
{
    public const byte StartByte = 0x68;
    /// <summary>Maximum APDU length field (control field + ASDU) [bytes].</summary>
    public const int MaxLength = 253;

    public static byte[] Encode(Apdu apdu)
    {
        byte[] asdu = apdu.Format == ApciFormat.I ? EncodeAsdu(apdu.Asdu ?? throw new ArgumentException("I-frame requires an ASDU.")) : [];
        if (4 + asdu.Length > MaxLength) throw new ArgumentException("APDU too long.");
        var b = new byte[6 + asdu.Length];
        b[0] = StartByte;
        b[1] = (byte)(4 + asdu.Length);
        switch (apdu.Format)
        {
            case ApciFormat.I:
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)((apdu.SendSequence & 0x7FFF) << 1));
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)((apdu.ReceiveSequence & 0x7FFF) << 1));
                break;
            case ApciFormat.S:
                b[2] = 0x01;
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)((apdu.ReceiveSequence & 0x7FFF) << 1));
                break;
            default:
                b[2] = (byte)(apdu.Function ?? throw new ArgumentException("U-frame requires a function."));
                break;
        }
        asdu.CopyTo(b, 6);
        return b;
    }

    /// <summary>Decodes one APDU; <paramref name="consumed"/> is its total length (use for stream framing).</summary>
    public static Apdu Decode(ReadOnlySpan<byte> data, out int consumed)
    {
        if (data.Length < 6 || data[0] != StartByte) throw new InvalidDataException("Not an IEC 104 APDU (start byte 0x68 expected).");
        int len = data[1];
        if (len < 4 || len > MaxLength || data.Length < 2 + len) throw new InvalidDataException("Invalid APDU length.");
        consumed = 2 + len;
        byte c1 = data[2];
        if ((c1 & 0x01) == 0)
        {
            ushort ns = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) >> 1);
            ushort nr = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) >> 1);
            return new Apdu(ApciFormat.I, ns, nr, null, DecodeAsdu(data.Slice(6, len - 4)));
        }
        if ((c1 & 0x03) == 0x01)
            return Apdu.S((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) >> 1));
        var f = (UFunction)c1;
        if (!Enum.IsDefined(f)) throw new InvalidDataException($"Invalid U-frame function 0x{c1:X2}.");
        return Apdu.U(f);
    }

    public static Apdu Decode(ReadOnlySpan<byte> data) => Decode(data, out _);

    /// <summary>Encodes an ASDU (SQ = 0: every object carries its own IOA).</summary>
    public static byte[] EncodeAsdu(Asdu a)
    {
        if (a.Objects.Count is 0 or > 127) throw new ArgumentException("ASDU must contain 1..127 information objects.");
        int es = Asdu.ElementSize(a.Type);
        var b = new byte[6 + a.Objects.Count * (3 + es)];
        b[0] = (byte)a.Type;
        b[1] = (byte)a.Objects.Count;
        b[2] = (byte)(((byte)a.Cause & 0x3F) | (a.Negative ? 0x40 : 0) | (a.Test ? 0x80 : 0));
        b[3] = a.OriginatorAddress;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), a.CommonAddress);
        int p = 6;
        foreach (var o in a.Objects)
        {
            b[p] = (byte)o.Address; b[p + 1] = (byte)(o.Address >> 8); b[p + 2] = (byte)(o.Address >> 16);
            p += 3;
            switch (a.Type)
            {
                case TypeId.M_SP_NA_1 or TypeId.M_SP_TB_1:
                    b[p++] = (byte)((o.State ? 1 : 0) | ((byte)o.Quality & 0xF0));
                    break;
                case TypeId.M_ME_NC_1 or TypeId.M_ME_TF_1:
                    BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p), (float)o.Value);
                    b[p + 4] = (byte)((byte)o.Quality & 0xF1);
                    p += 5;
                    break;
                default: // C_SC_NA_1 / C_SC_TA_1
                    b[p++] = (byte)((o.State ? 1 : 0) | ((o.Qualifier & 0x1F) << 2) | (o.Select ? 0x80 : 0));
                    break;
            }
            if (Asdu.HasTime(a.Type))
            {
                Cp56Time2a.Encode(o.Time ?? DateTimeOffset.UnixEpoch.AddYears(30), b.AsSpan(p), invalid: o.Time is null);
                p += 7;
            }
        }
        return b;
    }

    /// <summary>Decodes an ASDU (SQ = 0 or SQ = 1).</summary>
    public static Asdu DecodeAsdu(ReadOnlySpan<byte> b)
    {
        if (b.Length < 6) throw new InvalidDataException("ASDU too short.");
        var type = (TypeId)b[0];
        if (!Enum.IsDefined(type)) throw new NotSupportedException($"Type id {b[0]} not supported.");
        bool sq = (b[1] & 0x80) != 0;
        int n = b[1] & 0x7F;
        var cause = (CauseOfTransmission)(b[2] & 0x3F);
        bool neg = (b[2] & 0x40) != 0, test = (b[2] & 0x80) != 0;
        byte oa = b[3];
        ushort ca = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        int es = Asdu.ElementSize(type);
        int need = 6 + (sq ? 3 + n * es : n * (3 + es));
        if (b.Length < need) throw new InvalidDataException("Truncated ASDU.");
        var objs = new InformationObject[n];
        int p = 6;
        uint baseIoa = 0;
        for (int i = 0; i < n; i++)
        {
            uint ioa;
            if (!sq || i == 0)
            {
                ioa = (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16));
                baseIoa = ioa;
                p += 3;
            }
            else ioa = baseIoa + (uint)i;

            InformationObject o;
            switch (type)
            {
                case TypeId.M_SP_NA_1 or TypeId.M_SP_TB_1:
                    o = new(ioa, b[p] & 0x01, (Iec104Quality)(b[p] & 0xF0));
                    p += 1;
                    break;
                case TypeId.M_ME_NC_1 or TypeId.M_ME_TF_1:
                    o = new(ioa, BinaryPrimitives.ReadSingleLittleEndian(b[p..]), (Iec104Quality)(b[p + 4] & 0xF1));
                    p += 5;
                    break;
                default:
                    o = new(ioa, b[p] & 0x01, Select: (b[p] & 0x80) != 0, Qualifier: (byte)((b[p] >> 2) & 0x1F));
                    p += 1;
                    break;
            }
            if (Asdu.HasTime(type))
            {
                var t = Cp56Time2a.Decode(b.Slice(p, 7), out bool iv);
                o = o with { Time = t, Quality = iv ? o.Quality | Iec104Quality.Invalid : o.Quality };
                p += 7;
            }
            objs[i] = o;
        }
        return new Asdu(type, cause, ca, objs, neg, test, oa);
    }
}

/// <summary>
/// Send/receive sequence bookkeeping for one IEC 104 connection (k/w window counters). Helps a host build correctly numbered frames.
/// </summary>
public sealed class Iec104Sequence
{
    /// <summary>Next N(S) to send.</summary>
    public ushort SendSequence { get; private set; }
    /// <summary>Next expected N(S) from the peer (sent as N(R)).</summary>
    public ushort ReceiveSequence { get; private set; }
    /// <summary>I-frames received since the last acknowledgement we sent (compare with w, default 8).</summary>
    public int UnacknowledgedReceived { get; private set; }

    /// <summary>Builds the next numbered I-frame.</summary>
    public byte[] NextI(Asdu asdu)
    {
        var b = Iec104Codec.Encode(Apdu.I(SendSequence, ReceiveSequence, asdu));
        SendSequence = (ushort)((SendSequence + 1) & 0x7FFF);
        UnacknowledgedReceived = 0;
        return b;
    }

    /// <summary>Registers a received APDU; throws when an I-frame arrives out of sequence.</summary>
    public void OnReceived(Apdu apdu)
    {
        if (apdu.Format != ApciFormat.I) return;
        if (apdu.SendSequence != ReceiveSequence) throw new InvalidDataException($"Sequence error: expected N(S)={ReceiveSequence}, got {apdu.SendSequence}.");
        ReceiveSequence = (ushort)((ReceiveSequence + 1) & 0x7FFF);
        UnacknowledgedReceived++;
    }

    /// <summary>Builds an S-frame acknowledging everything received so far.</summary>
    public byte[] Acknowledge()
    {
        UnacknowledgedReceived = 0;
        return Iec104Codec.Encode(Apdu.S(ReceiveSequence));
    }
}
