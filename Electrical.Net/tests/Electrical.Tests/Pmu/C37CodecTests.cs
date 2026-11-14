using System.Numerics;
using System.Text;
using Electrical.Pmu.C37118;

namespace Electrical.Tests;

/// <summary>IEEE C37.118.2-2011 frame codec: CRC-CCITT check value, byte layout per Tables 2–15, round trips.</summary>
public class C37CodecTests
{
    // Independent bit-wise CRC-CCITT (0x1021, init 0xFFFF, no reflection) to cross-check the table-driven implementation.
    private static ushort BitwiseCrc(ReadOnlySpan<byte> data)
    {
        int crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return (ushort)crc;
    }

    [Fact]
    public void Crc_MatchesCcittCheckValue()
    {
        // CRC-16/CCITT-FALSE catalogue check value (C37.118.2-2011 §6.2 CHK: x^16 + x^12 + x^5 + 1, initial 0xFFFF).
        Assert.Equal(0x29B1, CrcCcitt.Compute("123456789"u8));
        Assert.Equal(0xFFFF, CrcCcitt.Compute([]));
        var rnd = new Random(7);
        var buf = new byte[300];
        rnd.NextBytes(buf);
        Assert.Equal(BitwiseCrc(buf), CrcCcitt.Compute(buf));
    }

    [Fact]
    public void CommandFrame_IsByteExact()
    {
        // Hand-assembled per Table 14: SYNC AA41 (command, version 1), FRAMESIZE 18, IDCODE 7734 (0x1E36), SOC 0x44853600,
        // FRACSEC 0, CMD 0x0002 (turn on transmission), CHK.
        byte[] body = Convert.FromHexString("AA4100121E364485360000000000" + "0002");
        ushort chk = BitwiseCrc(body);
        byte[] expected = [.. body, (byte)(chk >> 8), (byte)chk];
        var f = new C37CommandFrame(7734, new C37Time(0x44853600, 0), C37Command.DataOn) { Version = 1 };
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(C37Codec.Encode(f)));
        var back = C37Codec.DecodeCommand(expected);
        Assert.Equal((C37Command.DataOn, (ushort)7734, (byte)1), (back.Command, back.IdCode, back.Version));
        Assert.Equal(C37FrameType.Command, C37Codec.PeekType(expected));

        expected[15] ^= 0x01; // corrupt CMD → CHK mismatch
        Assert.False(C37Codec.IsValid(expected));
        Assert.Throws<InvalidDataException>(() => C37Codec.DecodeCommand(expected));
    }

    private static C37ConfigFrame SampleConfig(C37DataFormat format, C37FrameType type = C37FrameType.Config2) => new()
    {
        Type = type,
        IdCode = 7734,
        Time = new C37Time(1_700_000_000, 250_000, 0),
        TimeBase = 1_000_000,
        DataRate = 30,
        Pmus =
        [
            new PmuConfiguration
            {
                StationName = "Station A", IdCode = 7734, Format = format, NominalFrequencyHz = 60, ConfigCount = 3,
                Phasors = [new PhasorChannel("VA", PhasorKind.Voltage, 915527), new PhasorChannel("IA", PhasorKind.Current, 45776)],
                Analogs = [new AnalogChannel("ANALOG1", AnalogKind.Rms, 1)],
                Digitals = [DigitalWord.Named("BREAKER ") with { NormalMask = 0x0000, ValidMask = 0xFFFF }],
                Latitude = -6.2f, Longitude = 106.8f, Elevation = 8, ServiceClass = 'M', WindowMicroseconds = 100_000, GroupDelayMicroseconds = 50_000,
                GlobalPmuId = new Guid("01234567-89ab-cdef-0123-456789abcdef"),
            },
        ],
    };

    [Fact]
    public void Config2_LayoutAndRoundTrip()
    {
        var cfg = SampleConfig(C37DataFormat.Int16Rectangular);
        byte[] b = C37Codec.Encode(cfg);
        // 14 header + 4 TIME_BASE + 2 NUM_PMU + (16 STN + 2 ID + 2 FMT + 6 counts + 16·(2+1+16) names + 4·2 PHUNIT + 4 ANUNIT + 4 DIGUNIT + 2 FNOM + 2 CFGCNT) + 2 DATA_RATE + 2 CHK
        Assert.Equal(14 + 4 + 2 + (16 + 2 + 2 + 6 + 16 * 19 + 8 + 4 + 4 + 2 + 2) + 2 + 2, b.Length);
        Assert.Equal("AA32", Convert.ToHexString(b, 0, 2));
        Assert.Equal("Station A       ", Encoding.ASCII.GetString(b, 20, 16));
        Assert.Equal("000F4240", Convert.ToHexString(b, 14, 4)); // TIME_BASE 1 000 000
        Assert.Equal("000DF847", Convert.ToHexString(b, 20 + 16 + 2 + 2 + 6 + 16 * 19, 4)); // PHUNIT voltage, 915527
        Assert.Equal("0100B2D0", Convert.ToHexString(b, 20 + 16 + 2 + 2 + 6 + 16 * 19 + 4, 4)); // PHUNIT current, 45776
        var back = C37Codec.DecodeConfig(b);
        Assert.Equal(C37FrameType.Config2, back.Type);
        Assert.Equal((short)30, back.DataRate);
        var p = Assert.Single(back.Pmus);
        Assert.Equal(("Station A", (ushort)7734, 60.0, (ushort)3), (p.StationName, p.IdCode, p.NominalFrequencyHz, p.ConfigCount));
        Assert.Equal(["VA", "IA"], p.Phasors.Select(x => x.Name));
        Assert.Equal((PhasorKind.Current, 45776u), (p.Phasors[1].Kind, p.Phasors[1].ScaleFactor));
        Assert.Equal("BREAKER 15", p.Digitals[0].Names[15]);
        Assert.Equal(AnalogKind.Rms, p.Analogs[0].Kind);
        Assert.Equal(cfg.Time, back.Time);
    }

    [Fact]
    public void Config1And3_RoundTrip()
    {
        var c1 = C37Codec.DecodeConfig(C37Codec.Encode(SampleConfig(C37DataFormat.FloatPolar, C37FrameType.Config1)));
        Assert.Equal(C37FrameType.Config1, c1.Type);
        Assert.Equal(C37DataFormat.FloatPolar, c1.Pmus[0].Format);

        var c = SampleConfig(C37DataFormat.FloatPolar, C37FrameType.Config3);
        var cfg3 = c with { Pmus = [c.Pmus[0] with { StationName = "Gardu Induk Cawang 150 kV — a long CFG-3 name" }] };
        byte[] b = C37Codec.Encode(cfg3);
        Assert.Equal("AA52", Convert.ToHexString(b, 0, 2));
        var back = C37Codec.DecodeConfig(b);
        var p = back.Pmus[0];
        Assert.Equal(C37FrameType.Config3, back.Type);
        Assert.Equal("Gardu Induk Cawang 150 kV — a long CFG-3 name", p.StationName);
        Assert.Equal(cfg3.Pmus[0].GlobalPmuId, p.GlobalPmuId);
        Assert.Equal((-6.2f, 106.8f, 8f, 'M', 100_000, 50_000), (p.Latitude, p.Longitude, p.Elevation, p.ServiceClass, p.WindowMicroseconds, p.GroupDelayMicroseconds));
        Assert.Equal(PhasorKind.Current, p.Phasors[1].Kind);
        Assert.Equal(16, p.Digitals[0].Names.Count);
    }

    [Fact]
    public void DataFrame_Int16Rectangular_IsByteExact()
    {
        var cfg = SampleConfig(C37DataFormat.Int16Rectangular);
        var ph = cfg.Pmus[0].Phasors;
        // V = (14635 + j0)·PHUNIT, I = (−1000 + j500)·PHUNIT; f = 60.025 Hz → +25 mHz; ROCOF 0.12 Hz/s → 12; analog 77; digital 0x3C12
        var frame = new C37DataFrame(7734, new C37Time(0x44853600, 0x000041B1, 0x0F),
            [new PmuDataBlock(PmuStatus.None, [new Complex(14635 * ph[0].Resolution, 0), new Complex(-1000 * ph[1].Resolution, 500 * ph[1].Resolution)],
                60.025, 0.12, [77], [0x3C12])]);
        byte[] body = Convert.FromHexString(
            "AA02" + "0022" + "1E36" + "44853600" + "0F0041B1" + // SYNC (data, version 2), FRAMESIZE 34, IDCODE, SOC, FRACSEC (TQ 0x0F)
            "0000" + "392B" + "0000" + "FC18" + "01F4" + "0019" + "000C" + "004D" + "3C12");
        ushort chk = BitwiseCrc(body);
        byte[] expected = [.. body, (byte)(chk >> 8), (byte)chk];
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(C37Codec.Encode(frame, cfg)));

        var back = C37Codec.DecodeData(expected, cfg);
        var blk = back.Blocks[0];
        Assert.Equal(14635 * ph[0].Resolution, blk.Phasors[0].Real, 6);
        Assert.Equal(60.025, blk.FrequencyHz, 9);
        Assert.Equal(0.12, blk.RocofHzPerS, 9);
        Assert.Equal(77, blk.AnalogValues[0]);
        Assert.Equal((ushort)0x3C12, blk.DigitalValues[0]);
        Assert.Equal((byte)0x0F, back.Time.TimeQuality);
        Assert.Equal(0x41B1u, back.Time.FractionOfSecond);
    }

    [Theory]
    [InlineData(C37DataFormat.FloatPolar, 0.05)]        // float32 round-off at 87 kV
    [InlineData(C37DataFormat.FloatRectangular, 0.05)]
    [InlineData(C37DataFormat.PhasorPolar, 10.0)]       // int16 polar: magnitude quantised to PHUNIT (9.16 V), angle to 1e-4 rad
    public void DataFrame_RoundTripsAllFormats(C37DataFormat fmt, double tolV)
    {
        var cfg = SampleConfig(fmt);
        var v = Complex.FromPolarCoordinates(87_000, -0.4);
        var i = Complex.FromPolarCoordinates(450, 2.9);
        var t = C37Time.From(new DateTimeOffset(2026, 3, 4, 5, 6, 7, 120, TimeSpan.Zero));
        var frame = new C37DataFrame(7734, t, [new PmuDataBlock(PmuStatus.TestMode | PmuStatus.Trigger, [v, i], 59.973, -0.31, [1.5], [0xA5A5])]);
        var back = C37Codec.DecodeData(C37Codec.Encode(frame, cfg), cfg).Blocks[0];
        Assert.True((back.Phasors[0] - v).Magnitude < tolV, $"V error {(back.Phasors[0] - v).Magnitude}");
        Assert.True((back.Phasors[1] - i).Magnitude < Math.Max(tolV, 0.5), $"I error {(back.Phasors[1] - i).Magnitude}");
        Assert.Equal(59.973, back.FrequencyHz, 3);
        Assert.Equal(-0.31, back.RocofHzPerS, 2);
        Assert.Equal(PmuStatus.TestMode | PmuStatus.Trigger, back.Status);
        Assert.Equal(t.ToDateTime(), new DateTimeOffset(2026, 3, 4, 5, 6, 7, 120, TimeSpan.Zero));
    }

    [Fact]
    public async Task HeaderFrame_AndStreamFraming()
    {
        var h = new C37HeaderFrame(1, new C37Time(10, 0), "PMU header text");
        byte[] a = C37Codec.Encode(h);
        byte[] b = C37Codec.Encode(new C37CommandFrame(1, new C37Time(10, 0), C37Command.SendConfig2));
        using var ms = new MemoryStream([.. a, .. b]);
        var f1 = (await C37Codec.ReadFrameAsync(ms))!;
        var f2 = (await C37Codec.ReadFrameAsync(ms))!;
        Assert.Null(await C37Codec.ReadFrameAsync(ms));
        Assert.Equal("PMU header text", C37Codec.DecodeHeader(f1).Text);
        Assert.Equal(C37Command.SendConfig2, C37Codec.DecodeCommand(f2).Command);
        Assert.Equal("AA12", Convert.ToHexString(a, 0, 2));
    }
}
