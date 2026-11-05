using System.Buffers.Binary;
using System.Text.Json;

namespace Agritech.IoT;

/// <summary>Decoded Cayenne LPP value.</summary>
public sealed record LppValue(int Channel, int Type, string Name, double[] Values)
{
    public double Value => Values[0];
}

/// <summary>
/// Cayenne Low Power Payload (LPP) codec — the de-facto LoRaWAN sensor payload format (TTN, ChirpStack).
/// Supports digital/analog I/O, illuminance, presence, temperature, humidity, accelerometer, barometer, gyrometer and GPS.
/// </summary>
public static class CayenneLpp
{
    public const int DigitalInput = 0, DigitalOutput = 1, AnalogInput = 2, AnalogOutput = 3, Illuminance = 101, Presence = 102,
        Temperature = 103, Humidity = 104, Accelerometer = 113, Barometer = 115, Gyrometer = 134, Gps = 136;

    public static IReadOnlyList<LppValue> Decode(ReadOnlySpan<byte> p)
    {
        var res = new List<LppValue>();
        int i = 0;
        while (i + 2 <= p.Length)
        {
            int ch = p[i], type = p[i + 1];
            i += 2;
            switch (type)
            {
                case DigitalInput or DigitalOutput or Presence:
                    Need(p, i, 1); res.Add(new(ch, type, Name(type), [p[i]])); i += 1; break;
                case AnalogInput or AnalogOutput:
                    Need(p, i, 2); res.Add(new(ch, type, Name(type), [BinaryPrimitives.ReadInt16BigEndian(p[i..]) / 100.0])); i += 2; break;
                case Illuminance:
                    Need(p, i, 2); res.Add(new(ch, type, Name(type), [BinaryPrimitives.ReadUInt16BigEndian(p[i..])])); i += 2; break;
                case Temperature:
                    Need(p, i, 2); res.Add(new(ch, type, Name(type), [BinaryPrimitives.ReadInt16BigEndian(p[i..]) / 10.0])); i += 2; break;
                case Humidity:
                    Need(p, i, 1); res.Add(new(ch, type, Name(type), [p[i] / 2.0])); i += 1; break;
                case Barometer:
                    Need(p, i, 2); res.Add(new(ch, type, Name(type), [BinaryPrimitives.ReadUInt16BigEndian(p[i..]) / 10.0])); i += 2; break;
                case Accelerometer:
                    Need(p, i, 6);
                    res.Add(new(ch, type, Name(type), [BinaryPrimitives.ReadInt16BigEndian(p[i..]) / 1000.0, BinaryPrimitives.ReadInt16BigEndian(p[(i + 2)..]) / 1000.0, BinaryPrimitives.ReadInt16BigEndian(p[(i + 4)..]) / 1000.0]));
                    i += 6; break;
                case Gyrometer:
                    Need(p, i, 6);
                    res.Add(new(ch, type, Name(type), [BinaryPrimitives.ReadInt16BigEndian(p[i..]) / 100.0, BinaryPrimitives.ReadInt16BigEndian(p[(i + 2)..]) / 100.0, BinaryPrimitives.ReadInt16BigEndian(p[(i + 4)..]) / 100.0]));
                    i += 6; break;
                case Gps:
                    Need(p, i, 9);
                    res.Add(new(ch, type, Name(type), [Int24(p[i..]) / 10000.0, Int24(p[(i + 3)..]) / 10000.0, Int24(p[(i + 6)..]) / 100.0]));
                    i += 9; break;
                default:
                    throw new FormatException($"Unsupported Cayenne LPP type {type} at offset {i - 1}.");
            }
        }
        return res;
    }

    private static int Int24(ReadOnlySpan<byte> b)
    {
        int v = (b[0] << 16) | (b[1] << 8) | b[2];
        return (v & 0x800000) != 0 ? v - 0x1000000 : v;
    }

    private static void Need(ReadOnlySpan<byte> p, int i, int n)
    {
        if (i + n > p.Length) throw new FormatException("Truncated Cayenne LPP payload.");
    }

    public static string Name(int type) => type switch
    {
        DigitalInput => "digital_in", DigitalOutput => "digital_out", AnalogInput => "analog_in", AnalogOutput => "analog_out",
        Illuminance => "luminosity", Presence => "presence", Temperature => "temperature", Humidity => "relative_humidity",
        Accelerometer => "accelerometer", Barometer => "barometric_pressure", Gyrometer => "gyrometer", Gps => "gps", _ => $"type_{type}",
    };

    /// <summary>Fluent encoder (for device firmware simulators and tests).</summary>
    public sealed class Writer
    {
        private readonly List<byte> _b = [];
        public Writer Temperature(int ch, double c) { short v = (short)Math.Round(c * 10); _b.AddRange([(byte)ch, CayenneLpp.Temperature, (byte)(v >> 8), (byte)v]); return this; }
        public Writer Humidity(int ch, double pct) { _b.AddRange([(byte)ch, CayenneLpp.Humidity, (byte)Math.Round(pct * 2)]); return this; }
        public Writer Analog(int ch, double v) { short s = (short)Math.Round(v * 100); _b.AddRange([(byte)ch, AnalogInput, (byte)(s >> 8), (byte)s]); return this; }
        public Writer Illuminance(int ch, ushort lux) { _b.AddRange([(byte)ch, CayenneLpp.Illuminance, (byte)(lux >> 8), (byte)lux]); return this; }
        public Writer Barometer(int ch, double hPa) { ushort v = (ushort)Math.Round(hPa * 10); _b.AddRange([(byte)ch, CayenneLpp.Barometer, (byte)(v >> 8), (byte)v]); return this; }
        public Writer Digital(int ch, byte v) { _b.AddRange([(byte)ch, DigitalInput, v]); return this; }
        public Writer Gps(int ch, double lat, double lon, double altM)
        {
            _b.AddRange([(byte)ch, CayenneLpp.Gps]);
            foreach (var v in new[] { (int)Math.Round(lat * 10000), (int)Math.Round(lon * 10000), (int)Math.Round(altM * 100) })
                _b.AddRange([(byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            return this;
        }
        public byte[] ToArray() => [.. _b];
    }
}

/// <summary>Uplink parsed from a LoRaWAN network server webhook.</summary>
public sealed record LoRaUplink(string DeviceId, string? DevEui, int FPort, int FCnt, byte[] Payload, DateTimeOffset ReceivedAt, double? Rssi, double? Snr);

/// <summary>The Things Stack (TTN v3) webhook parsing and mapping of LPP channels to agronomic sensors.</summary>
public static class TtnWebhook
{
    public static LoRaUplink ParseUplink(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var ids = r.GetProperty("end_device_ids");
        var up = r.GetProperty("uplink_message");
        double? rssi = null, snr = null;
        if (up.TryGetProperty("rx_metadata", out var md) && md.GetArrayLength() > 0)
        {
            var m0 = md[0];
            if (m0.TryGetProperty("rssi", out var rs)) rssi = rs.GetDouble();
            if (m0.TryGetProperty("snr", out var sn)) snr = sn.GetDouble();
        }
        return new LoRaUplink(
            ids.GetProperty("device_id").GetString()!,
            ids.TryGetProperty("dev_eui", out var eui) ? eui.GetString() : null,
            up.TryGetProperty("f_port", out var fp) ? fp.GetInt32() : 0,
            up.TryGetProperty("f_cnt", out var fc) ? fc.GetInt32() : 0,
            up.TryGetProperty("frm_payload", out var pl) ? Convert.FromBase64String(pl.GetString()!) : [],
            r.TryGetProperty("received_at", out var ra) ? ra.GetDateTimeOffset() : DateTimeOffset.UtcNow,
            rssi, snr);
    }

    /// <summary>Converts LPP values into readings; <paramref name="channelMap"/> maps LPP channels to semantic kinds (e.g. analog ch 3 → soil moisture).</summary>
    public static IReadOnlyList<SensorReading> ToReadings(LoRaUplink up, IReadOnlyDictionary<int, SensorKind>? channelMap = null)
    {
        var list = new List<SensorReading>();
        foreach (var v in CayenneLpp.Decode(up.Payload))
        {
            SensorKind kind = channelMap is not null && channelMap.TryGetValue(v.Channel, out var k) ? k : v.Type switch
            {
                CayenneLpp.Temperature => SensorKind.AirTemperature,
                CayenneLpp.Humidity => SensorKind.RelativeHumidity,
                CayenneLpp.Barometer => SensorKind.Barometer,
                CayenneLpp.Illuminance => SensorKind.Illuminance,
                CayenneLpp.AnalogInput => SensorKind.Analog,
                CayenneLpp.Gps => SensorKind.Gps,
                _ => SensorKind.Digital,
            };
            list.Add(new SensorReading(up.DeviceId, kind, v.Value, SensorReading.DefaultUnit(kind), up.ReceivedAt, v.Channel));
        }
        return list;
    }
}
