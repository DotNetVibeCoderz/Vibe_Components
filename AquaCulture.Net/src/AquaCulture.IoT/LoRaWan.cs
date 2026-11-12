using System.Buffers.Binary;

namespace AquaCulture.IoT;

/// <summary>A decoded Cayenne LPP channel.</summary>
public sealed record LppValue(byte Channel, byte Type, string TypeName, double[] Values);

/// <summary>
/// LoRaWAN payload codecs: Cayenne Low Power Payload (as used by The Things Network / ChirpStack) and the compact
/// AquaCulture.Net pond payload (12 bytes: DO, temperature, pH, salinity, TAN, battery).
/// </summary>
public static class LoRaPayload
{
    /// <summary>Decodes Cayenne LPP v1 (digital/analog I/O, temperature, humidity, barometer, GPS, …).</summary>
    public static IReadOnlyList<LppValue> DecodeCayenne(ReadOnlySpan<byte> p)
    {
        var list = new List<LppValue>();
        int i = 0;
        while (i + 2 <= p.Length)
        {
            byte ch = p[i], type = p[i + 1];
            i += 2;
            (string name, int size) = type switch
            {
                0 => ("digital_input", 1), 1 => ("digital_output", 1), 2 => ("analog_input", 2), 3 => ("analog_output", 2),
                101 => ("illuminance", 2), 102 => ("presence", 1), 103 => ("temperature", 2), 104 => ("humidity", 1),
                113 => ("accelerometer", 6), 115 => ("barometer", 2), 136 => ("gps", 9),
                _ => throw new FormatException($"Unknown Cayenne LPP type {type} at byte {i - 1}."),
            };
            if (i + size > p.Length) throw new FormatException("Truncated Cayenne LPP payload.");
            var s = p.Slice(i, size);
            double[] values = type switch
            {
                0 or 1 or 102 => [s[0]],
                2 or 3 => [BinaryPrimitives.ReadInt16BigEndian(s) / 100.0],
                101 => [BinaryPrimitives.ReadUInt16BigEndian(s)],
                103 => [BinaryPrimitives.ReadInt16BigEndian(s) / 10.0],
                104 => [s[0] / 2.0],
                113 => [BinaryPrimitives.ReadInt16BigEndian(s) / 1000.0, BinaryPrimitives.ReadInt16BigEndian(s[2..]) / 1000.0, BinaryPrimitives.ReadInt16BigEndian(s[4..]) / 1000.0],
                115 => [BinaryPrimitives.ReadUInt16BigEndian(s) / 10.0],
                _ => [Int24(s) / 10000.0, Int24(s[3..]) / 10000.0, Int24(s[6..]) / 100.0],
            };
            list.Add(new LppValue(ch, type, name, values));
            i += size;
        }
        return list;
    }

    private static int Int24(ReadOnlySpan<byte> b) { int v = b[0] << 16 | b[1] << 8 | b[2]; return (v & 0x800000) != 0 ? v - 0x1000000 : v; }

    /// <summary>Encodes a Cayenne temperature (type 103) channel.</summary>
    public static byte[] CayenneTemperature(byte channel, double celsius)
    {
        var b = new byte[4]; b[0] = channel; b[1] = 103;
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), (short)Math.Round(celsius * 10));
        return b;
    }

    /// <summary>Encodes a Cayenne analog-input (type 2) channel (value × 100).</summary>
    public static byte[] CayenneAnalog(byte channel, double value)
    {
        var b = new byte[4]; b[0] = channel; b[1] = 2;
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), (short)Math.Round(value * 100));
        return b;
    }

    /// <summary>
    /// Channel convention for aquaculture nodes using Cayenne LPP: ch1 temperature, ch2 DO (analog), ch3 pH (analog), ch4 salinity (analog),
    /// ch5 TAN (analog), ch6 battery (analog). Converts LPP values into readings.
    /// </summary>
    public static IReadOnlyList<SensorReading> CayenneToReadings(IReadOnlyList<LppValue> values, string deviceId, string pondId, DateTime ts)
    {
        var list = new List<SensorReading>();
        foreach (var v in values)
        {
            SensorKind? kind = (v.Channel, v.TypeName) switch
            {
                (_, "temperature") => SensorKind.Temperature,
                (2, "analog_input") => SensorKind.DissolvedOxygen,
                (3, "analog_input") => SensorKind.Ph,
                (4, "analog_input") => SensorKind.Salinity,
                (5, "analog_input") => SensorKind.Ammonia,
                (6, "analog_input") => SensorKind.Battery,
                (_, "humidity") => SensorKind.Humidity,
                _ => null,
            };
            if (kind is { } k) list.Add(new SensorReading(deviceId, pondId, k, v.Values[0], ts, SensorReading.DefaultUnit(k)));
        }
        return list;
    }

    /// <summary>Compact 12-byte pond payload: DO×100, T×100 (int16), pH×100, salinity×10, TAN×1000, battery mV (all big-endian uint16 except T).</summary>
    public static byte[] EncodePond(double doMgL, double tempC, double ph, double salinityPpt, double tanMgL, double batteryV)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Round(doMgL * 100));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2), (short)Math.Round(tempC * 100));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)Math.Round(ph * 100));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)Math.Round(salinityPpt * 10));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), (ushort)Math.Round(tanMgL * 1000));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), (ushort)Math.Round(batteryV * 1000));
        return b;
    }

    public static IReadOnlyList<SensorReading> DecodePond(ReadOnlySpan<byte> b, string deviceId, string pondId, DateTime ts)
    {
        if (b.Length != 12) throw new FormatException("Pond payload must be 12 bytes.");
        SensorReading R(SensorKind k, double v) => new(deviceId, pondId, k, v, ts, SensorReading.DefaultUnit(k));
        return
        [
            R(SensorKind.DissolvedOxygen, BinaryPrimitives.ReadUInt16BigEndian(b) / 100.0),
            R(SensorKind.Temperature, BinaryPrimitives.ReadInt16BigEndian(b[2..]) / 100.0),
            R(SensorKind.Ph, BinaryPrimitives.ReadUInt16BigEndian(b[4..]) / 100.0),
            R(SensorKind.Salinity, BinaryPrimitives.ReadUInt16BigEndian(b[6..]) / 10.0),
            R(SensorKind.Ammonia, BinaryPrimitives.ReadUInt16BigEndian(b[8..]) / 1000.0),
            R(SensorKind.Battery, BinaryPrimitives.ReadUInt16BigEndian(b[10..]) / 1000.0),
        ];
    }

    /// <summary>Parses a The Things Stack v3 uplink webhook JSON (frm_payload base64) with the compact pond codec.</summary>
    public static IReadOnlyList<SensorReading> ParseTtnUplink(string json, string pondId)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        string dev = r.GetProperty("end_device_ids").GetProperty("device_id").GetString() ?? "";
        var up = r.GetProperty("uplink_message");
        var bytes = Convert.FromBase64String(up.GetProperty("frm_payload").GetString() ?? "");
        var ts = r.TryGetProperty("received_at", out var ra) && ra.TryGetDateTime(out var t) ? t.ToUniversalTime() : DateTime.UtcNow;
        return bytes.Length == 12 ? DecodePond(bytes, dev, pondId, ts) : CayenneToReadings(DecodeCayenne(bytes), dev, pondId, ts);
    }
}
