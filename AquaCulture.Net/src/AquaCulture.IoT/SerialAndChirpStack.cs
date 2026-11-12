using System.IO.Ports;
using System.Text.Json;

namespace AquaCulture.IoT;

/// <summary>
/// Modbus RTU master over a serial line (RS-485 probes and PLCs) or any <see cref="Stream"/>. One request at a time;
/// responses are validated (unit, function, CRC) by <see cref="Modbus.ParseReadResponseRtu"/>.
/// </summary>
public sealed class ModbusRtuClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Wraps an open stream (for example a TCP-to-RS485 converter or a test pipe).</summary>
    public ModbusRtuClient(Stream stream, IDisposable? owner = null) { _stream = stream; _owner = owner; }

    /// <summary>Response timeout per request.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Opens a serial port (8 data bits, no parity, one stop bit by default — the usual setting for water-quality probes).</summary>
    public static ModbusRtuClient OpenSerial(string portName, int baudRate = 9600, Parity parity = Parity.None, StopBits stopBits = StopBits.One)
    {
        var port = new SerialPort(portName, baudRate, parity, 8, stopBits) { ReadTimeout = 1000, WriteTimeout = 1000 };
        port.Open();
        return new ModbusRtuClient(port.BaseStream, port);
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(byte unit, ushort address, ushort count, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await _stream.WriteAsync(Modbus.ReadHoldingRegistersRtu(unit, address, count), timeout.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            var head = new byte[3];
            await _stream.ReadExactlyAsync(head, timeout.Token).ConfigureAwait(false);
            int rest = (head[1] & 0x80) != 0 ? 2 : head[2] + 2;   // exception frames carry 1 code byte (already read) + CRC
            var tail = new byte[rest];
            await _stream.ReadExactlyAsync(tail, timeout.Token).ConfigureAwait(false);
            byte[] frame = [.. head, .. tail];
            if ((head[1] & 0x80) != 0) throw new InvalidOperationException($"Modbus exception code {head[2]} from unit {head[0]}.");
            return Modbus.ParseReadResponseRtu(frame, unit);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads a probe and returns readings using a register map.</summary>
    public async Task<IReadOnlyList<SensorReading>> ReadProbeAsync(byte unit, string deviceId, string pondId, IReadOnlyList<ModbusRegisterMap> map, CancellationToken ct = default)
    {
        ushort start = map.Min(m => m.Address), end = (ushort)map.Max(m => m.Address + m.Registers);
        var regs = await ReadHoldingRegistersAsync(unit, start, (ushort)(end - start), ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        return Modbus.Decode(regs, start, map).Select(x => new SensorReading(deviceId, pondId, x.Kind, x.Value, now, SensorReading.DefaultUnit(x.Kind))).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_owner is not null) _owner.Dispose(); else await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>ChirpStack v4 integration events.</summary>
public static class ChirpStack
{
    /// <summary>
    /// Parses a ChirpStack v4 HTTP-integration <c>up</c> event (<c>deviceInfo.devEui</c>, <c>time</c>, base64 <c>data</c>).
    /// The payload is decoded with the 12-byte pond codec or Cayenne LPP; if ChirpStack already decoded it (<c>object</c>),
    /// numeric properties with recognised names are used instead. <paramref name="pondByDevEui"/> maps devices to ponds.
    /// </summary>
    public static IReadOnlyList<SensorReading> ParseUplink(string json, Func<string, string>? pondByDevEui = null)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var info = r.GetProperty("deviceInfo");
        string devEui = info.TryGetProperty("devEui", out var d) ? d.GetString() ?? "" : "";
        string name = info.TryGetProperty("deviceName", out var n) ? n.GetString() ?? devEui : devEui;
        string pond = pondByDevEui?.Invoke(devEui) ?? (info.TryGetProperty("tags", out var tags) && tags.TryGetProperty("pond", out var p) ? p.GetString() ?? "" : "");
        var ts = r.TryGetProperty("time", out var t) && t.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;

        if (r.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            var list = new List<SensorReading>();
            foreach (var prop in obj.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Number && SensorReading.TryParseKind(prop.Name, out var kind))
                    list.Add(new SensorReading(name, pond, kind, prop.Value.GetDouble(), ts, SensorReading.DefaultUnit(kind)));
            if (list.Count > 0) return list;
        }
        var bytes = Convert.FromBase64String(r.TryGetProperty("data", out var data) ? data.GetString() ?? "" : "");
        return bytes.Length == 12 ? LoRaPayload.DecodePond(bytes, name, pond, ts) : LoRaPayload.CayenneToReadings(LoRaPayload.DecodeCayenne(bytes), name, pond, ts);
    }

    /// <summary>True for ChirpStack <c>up</c> events (the HTTP integration posts several event types to one URL with <c>?event=up</c>).</summary>
    public static bool IsUplink(string eventQueryValue) => eventQueryValue.Equals("up", StringComparison.OrdinalIgnoreCase);
}
