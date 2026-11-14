using System.Numerics;

namespace Electrical.Pmu.C37118;

/// <summary>Frame type encoded in bits 6–4 of the second SYNC byte (IEEE C37.118.2-2011 Table 2).</summary>
public enum C37FrameType : byte
{
    /// <summary>Data frame (0xAA0x).</summary>
    Data = 0,
    /// <summary>Header frame, human-readable text (0xAA1x).</summary>
    Header = 1,
    /// <summary>Configuration frame 1: PMU capabilities (0xAA2x).</summary>
    Config1 = 2,
    /// <summary>Configuration frame 2: measurements being transmitted (0xAA3x).</summary>
    Config2 = 3,
    /// <summary>Command frame (0xAA4x).</summary>
    Command = 4,
    /// <summary>Configuration frame 3: extended configuration with variable-length names, scaling and location (0xAA5x).</summary>
    Config3 = 5,
}

/// <summary>Command codes of the CMD field (C37.118.2-2011 Table 15).</summary>
public enum C37Command : ushort
{
    /// <summary>Turn off transmission of data frames.</summary>
    DataOff = 0x0001,
    /// <summary>Turn on transmission of data frames.</summary>
    DataOn = 0x0002,
    /// <summary>Send the header frame.</summary>
    SendHeader = 0x0003,
    /// <summary>Send CFG-1.</summary>
    SendConfig1 = 0x0004,
    /// <summary>Send CFG-2.</summary>
    SendConfig2 = 0x0005,
    /// <summary>Send CFG-3.</summary>
    SendConfig3 = 0x0006,
    /// <summary>Extended frame (user data in EXTFRAME).</summary>
    ExtendedFrame = 0x0008,
}

/// <summary>Data format flags of a PMU block (FORMAT field, C37.118.2-2011 Table 8). Clear bits mean 16-bit integer / rectangular.</summary>
[Flags]
public enum C37DataFormat : ushort
{
    /// <summary>Phasors, analogs and frequency as 16-bit integers, phasors rectangular.</summary>
    Int16Rectangular = 0,
    /// <summary>Bit 0: phasors in polar form (magnitude, angle) instead of rectangular (real, imaginary).</summary>
    PhasorPolar = 0x01,
    /// <summary>Bit 1: phasors as 32-bit IEEE floats.</summary>
    PhasorFloat = 0x02,
    /// <summary>Bit 2: analogs as 32-bit IEEE floats.</summary>
    AnalogFloat = 0x04,
    /// <summary>Bit 3: FREQ/DFREQ as 32-bit IEEE floats.</summary>
    FrequencyFloat = 0x08,
    /// <summary>All values as float, phasors polar.</summary>
    FloatPolar = PhasorPolar | PhasorFloat | AnalogFloat | FrequencyFloat,
    /// <summary>All values as float, phasors rectangular.</summary>
    FloatRectangular = PhasorFloat | AnalogFloat | FrequencyFloat,
}

/// <summary>Selected bits of the 16-bit STAT word of a data block (C37.118.2-2011 Table 7).</summary>
[Flags]
public enum PmuStatus : ushort
{
    /// <summary>All good.</summary>
    None = 0,
    /// <summary>Bit 15–14 = 01: PMU error, data invalid.</summary>
    DataInvalid = 0x4000,
    /// <summary>Bit 15–14 = 10: PMU in test mode.</summary>
    TestMode = 0x8000,
    /// <summary>Bit 13: PMU not synchronised to UTC.</summary>
    NotSynchronized = 0x2000,
    /// <summary>Bit 12: data sorted by arrival (not by timestamp) — set by a PDC for late/missing data.</summary>
    SortedByArrival = 0x1000,
    /// <summary>Bit 11: PMU trigger detected.</summary>
    Trigger = 0x0800,
    /// <summary>Bit 10: configuration change within one minute.</summary>
    ConfigChange = 0x0400,
    /// <summary>Bit 9: data modified by post-processing.</summary>
    DataModified = 0x0200,
}

/// <summary>Kind of a phasor channel (PHUNIT most significant byte).</summary>
public enum PhasorKind : byte
{
    /// <summary>Voltage phasor [V].</summary>
    Voltage = 0,
    /// <summary>Current phasor [A].</summary>
    Current = 1,
}

/// <summary>Kind of an analog channel (ANUNIT most significant byte).</summary>
public enum AnalogKind : byte
{
    /// <summary>Single point-on-wave.</summary>
    PointOnWave = 0,
    /// <summary>RMS of analog input.</summary>
    Rms = 1,
    /// <summary>Peak of analog input.</summary>
    Peak = 2,
}

/// <summary>
/// One phasor channel. <paramref name="ScaleFactor"/> is the PHUNIT conversion factor for 16-bit integer data in 10⁻⁵ V (or A) per bit
/// (ignored for float data). CFG-3 adds a magnitude scale and an angle offset [rad] (PHSCALE).
/// </summary>
public sealed record PhasorChannel(string Name, PhasorKind Kind = PhasorKind.Voltage, uint ScaleFactor = 100000)
{
    /// <summary>CFG-3 PHSCALE magnitude scale factor (1 = none).</summary>
    public float MagnitudeScale { get; init; } = 1f;
    /// <summary>CFG-3 PHSCALE angle adjustment [rad].</summary>
    public float AngleOffsetRad { get; init; }
    /// <summary>CFG-3 phasor component code (0 zero, 1 positive, 2 negative sequence, 4/5/6 phase A/B/C).</summary>
    public byte Component { get; init; } = 1;
    /// <summary>Engineering value of one integer count [V or A].</summary>
    public double Resolution => ScaleFactor * 1e-5;
}

/// <summary>One analog channel; integer values are multiplied by <paramref name="ScaleFactor"/> (user-defined 24-bit signed scaling).</summary>
public sealed record AnalogChannel(string Name, AnalogKind Kind = AnalogKind.PointOnWave, int ScaleFactor = 1)
{
    /// <summary>CFG-3 ANSCALE offset added after scaling.</summary>
    public float Offset { get; init; }
}

/// <summary>One 16-bit digital status word with 16 channel names and the DIGUNIT masks.</summary>
public sealed record DigitalWord(IReadOnlyList<string> Names, ushort NormalMask = 0, ushort ValidMask = 0xFFFF)
{
    /// <summary>Creates a word whose 16 channels are called <c>{prefix}0 … {prefix}15</c>.</summary>
    public static DigitalWord Named(string prefix) => new(Enumerable.Range(0, 16).Select(i => $"{prefix}{i}").ToArray());
}

/// <summary>Configuration of one PMU block inside a configuration frame.</summary>
public sealed record PmuConfiguration
{
    /// <summary>Station name (16 ASCII characters in CFG-1/2, up to 255 in CFG-3).</summary>
    public required string StationName { get; init; }
    /// <summary>Data source id of this PMU block.</summary>
    public required ushort IdCode { get; init; }
    public C37DataFormat Format { get; init; } = C37DataFormat.FloatPolar;
    public IReadOnlyList<PhasorChannel> Phasors { get; init; } = [];
    public IReadOnlyList<AnalogChannel> Analogs { get; init; } = [];
    public IReadOnlyList<DigitalWord> Digitals { get; init; } = [];
    /// <summary>Nominal frequency [Hz]: 50 or 60.</summary>
    public double NominalFrequencyHz { get; init; } = 50;
    /// <summary>Configuration change count.</summary>
    public ushort ConfigCount { get; init; }

    // CFG-3 only
    /// <summary>CFG-3 global PMU id (16 bytes).</summary>
    public Guid GlobalPmuId { get; init; }
    /// <summary>CFG-3 latitude [degrees, WGS84] (NaN = unspecified).</summary>
    public float Latitude { get; init; } = float.NaN;
    /// <summary>CFG-3 longitude [degrees, WGS84] (NaN = unspecified).</summary>
    public float Longitude { get; init; } = float.NaN;
    /// <summary>CFG-3 elevation [m] (NaN = unspecified).</summary>
    public float Elevation { get; init; } = float.NaN;
    /// <summary>CFG-3 service class: 'P' or 'M'.</summary>
    public char ServiceClass { get; init; } = 'P';
    /// <summary>CFG-3 measurement window length [µs].</summary>
    public int WindowMicroseconds { get; init; }
    /// <summary>CFG-3 group delay [µs].</summary>
    public int GroupDelayMicroseconds { get; init; }

    /// <summary>Size of this block in a data frame [bytes].</summary>
    public int DataBlockSize =>
        2 + Phasors.Count * ((Format & C37DataFormat.PhasorFloat) != 0 ? 8 : 4)
          + 2 * ((Format & C37DataFormat.FrequencyFloat) != 0 ? 4 : 2)
          + Analogs.Count * ((Format & C37DataFormat.AnalogFloat) != 0 ? 4 : 2)
          + Digitals.Count * 2;
}

/// <summary>Time fields shared by every frame: SOC (UTC seconds since 1970) and FRACSEC (24-bit fraction count + time-quality byte).</summary>
/// <param name="Soc">Second-of-century counter [s since 1970-01-01 UTC].</param>
/// <param name="FractionOfSecond">Fraction of second count (0 ≤ value &lt; TIME_BASE).</param>
/// <param name="TimeQuality">FRACSEC most significant byte: bits 6–4 leap-second flags, bits 3–0 time-quality code (0 = locked to UTC, 0xF = fault).</param>
public readonly record struct C37Time(uint Soc, uint FractionOfSecond, byte TimeQuality = 0)
{
    /// <summary>Default TIME_BASE: microsecond resolution.</summary>
    public const uint DefaultTimeBase = 1_000_000;

    /// <summary>Converts a UTC time to SOC/FRACSEC with the given time base (fraction rounded to the nearest count).</summary>
    public static C37Time From(DateTimeOffset time, uint timeBase = DefaultTimeBase, byte timeQuality = 0)
    {
        long ticks = (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks);
        long soc = ticks / TimeSpan.TicksPerSecond;
        long rem = ticks - soc * TimeSpan.TicksPerSecond;
        long frac = (long)Math.Round(rem * (double)timeBase / TimeSpan.TicksPerSecond);
        if (frac >= timeBase) { soc++; frac -= timeBase; }
        return new C37Time((uint)soc, (uint)frac, timeQuality);
    }

    /// <summary>Converts back to UTC with the given time base.</summary>
    public DateTimeOffset ToDateTime(uint timeBase = DefaultTimeBase) =>
        DateTimeOffset.UnixEpoch.AddTicks(Soc * TimeSpan.TicksPerSecond + (long)Math.Round(FractionOfSecond * (double)TimeSpan.TicksPerSecond / timeBase));

    /// <summary>Sortable key (seconds × 2²⁴ + fraction) independent of the time base for a single stream.</summary>
    public ulong Key => ((ulong)Soc << 24) | (FractionOfSecond & 0xFFFFFF);

    internal uint FracSecField => ((uint)TimeQuality << 24) | (FractionOfSecond & 0xFFFFFF);
    internal static C37Time FromFields(uint soc, uint fracsec) => new(soc, fracsec & 0xFFFFFF, (byte)(fracsec >> 24));
}

/// <summary>Configuration frame (CFG-1, CFG-2 or CFG-3).</summary>
public sealed record C37ConfigFrame
{
    /// <summary><see cref="C37FrameType.Config1"/>, <see cref="C37FrameType.Config2"/> or <see cref="C37FrameType.Config3"/>.</summary>
    public C37FrameType Type { get; init; } = C37FrameType.Config2;
    /// <summary>Stream source id (IDCODE of the frame).</summary>
    public required ushort IdCode { get; init; }
    public C37Time Time { get; init; }
    /// <summary>Resolution of FRACSEC (counts per second).</summary>
    public uint TimeBase { get; init; } = C37Time.DefaultTimeBase;
    public required IReadOnlyList<PmuConfiguration> Pmus { get; init; }
    /// <summary>DATA_RATE: &gt; 0 frames per second, &lt; 0 seconds per frame.</summary>
    public short DataRate { get; init; } = 50;
    /// <summary>Protocol version (1 = C37.118-2005, 2 = C37.118.2-2011).</summary>
    public byte Version { get; init; } = 2;
    /// <summary>CFG-3 continuation index (0 = single fragment).</summary>
    public ushort ContinuationIndex { get; init; }

    /// <summary>Reporting interval [s].</summary>
    public double ReportingIntervalS => DataRate > 0 ? 1.0 / DataRate : -DataRate;
}

/// <summary>
/// Measurements of one PMU block. Phasors are RMS in engineering units [V or A] with angles relative to the UTC-synchronised
/// nominal-frequency cosine; <see cref="FrequencyHz"/> is the actual frequency [Hz] and <see cref="RocofHzPerS"/> its rate of change [Hz/s].
/// </summary>
public sealed record PmuDataBlock(PmuStatus Status, IReadOnlyList<Complex> Phasors, double FrequencyHz, double RocofHzPerS,
    IReadOnlyList<double>? Analogs = null, IReadOnlyList<ushort>? Digitals = null)
{
    /// <summary>Raw STAT word (bits beyond <see cref="PmuStatus"/>, e.g. trigger reason, are preserved here).</summary>
    public ushort RawStatus { get; init; } = (ushort)Status;
    public IReadOnlyList<double> AnalogValues => Analogs ?? [];
    public IReadOnlyList<ushort> DigitalValues => Digitals ?? [];
}

/// <summary>Data frame: one block per PMU in the matching configuration.</summary>
public sealed record C37DataFrame(ushort IdCode, C37Time Time, IReadOnlyList<PmuDataBlock> Blocks)
{
    public byte Version { get; init; } = 2;
}

/// <summary>Header frame (free ASCII text).</summary>
public sealed record C37HeaderFrame(ushort IdCode, C37Time Time, string Text)
{
    public byte Version { get; init; } = 2;
}

/// <summary>Command frame sent by a PDC to a PMU.</summary>
public sealed record C37CommandFrame(ushort IdCode, C37Time Time, C37Command Command, byte[]? ExtendedData = null)
{
    public byte Version { get; init; } = 2;
}
