using System.Buffers.Binary;
using System.Net.Sockets;

namespace AquaCulture.IoT;

/// <summary>Register data type in a Modbus map.</summary>
public enum RegisterType { UInt16, Int16, UInt32, Int32, Float32 }

/// <summary>Maps holding registers of an industrial probe or PLC to a sensor value: value = raw × Scale + Offset.</summary>
public sealed record ModbusRegisterMap(SensorKind Kind, ushort Address, RegisterType Type = RegisterType.UInt16, double Scale = 1, double Offset = 0, bool WordSwap = false)
{
    public int Registers => Type is RegisterType.UInt32 or RegisterType.Int32 or RegisterType.Float32 ? 2 : 1;
}

/// <summary>
/// Modbus RTU / TCP framing for function 0x03 (read holding registers) and 0x06 (write single register), with CRC-16/MODBUS.
/// Pure and allocation-light so it runs on gateways (Raspberry Pi) and in tests without hardware.
/// </summary>
public static class Modbus
{
    /// <summary>CRC-16/MODBUS (poly 0xA001 reflected, init 0xFFFF).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>RTU request: [unit][0x03][addr hi][addr lo][count hi][count lo][crc lo][crc hi].</summary>
    public static byte[] ReadHoldingRegistersRtu(byte unit, ushort address, ushort count)
    {
        var f = new byte[8];
        f[0] = unit; f[1] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), address);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), count);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(6), Crc16(f.AsSpan(0, 6)));
        return f;
    }

    /// <summary>RTU write-single-register request (function 0x06), e.g. switching an aerator relay.</summary>
    public static byte[] WriteSingleRegisterRtu(byte unit, ushort address, ushort value)
    {
        var f = new byte[8];
        f[0] = unit; f[1] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), address);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), value);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(6), Crc16(f.AsSpan(0, 6)));
        return f;
    }

    /// <summary>Validates an RTU 0x03 response and returns its registers.</summary>
    public static ushort[] ParseReadResponseRtu(ReadOnlySpan<byte> frame, byte expectedUnit)
    {
        if (frame.Length < 5) throw new FormatException("Frame too short.");
        ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        if (crc != Crc16(frame[..^2])) throw new FormatException("CRC mismatch.");
        if (frame[0] != expectedUnit) throw new FormatException($"Unexpected unit {frame[0]}.");
        if ((frame[1] & 0x80) != 0) throw new InvalidOperationException($"Modbus exception code {frame[2]}.");
        if (frame[1] != 0x03) throw new FormatException($"Unexpected function 0x{frame[1]:X2}.");
        int bytes = frame[2];
        if (frame.Length != 3 + bytes + 2) throw new FormatException("Length mismatch.");
        var regs = new ushort[bytes / 2];
        for (int i = 0; i < regs.Length; i++) regs[i] = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3 + 2 * i, 2));
        return regs;
    }

    /// <summary>Builds an RTU 0x03 response (for simulators and tests).</summary>
    public static byte[] BuildReadResponseRtu(byte unit, ReadOnlySpan<ushort> registers)
    {
        var f = new byte[3 + registers.Length * 2 + 2];
        f[0] = unit; f[1] = 0x03; f[2] = (byte)(registers.Length * 2);
        for (int i = 0; i < registers.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(3 + 2 * i), registers[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(f.Length - 2), Crc16(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    /// <summary>Modbus TCP (MBAP) read request.</summary>
    public static byte[] ReadHoldingRegistersTcp(ushort transactionId, byte unit, ushort address, ushort count)
    {
        var f = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(f, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 6);
        f[6] = unit; f[7] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(10), count);
        return f;
    }

    public static ushort[] ParseReadResponseTcp(ReadOnlySpan<byte> frame, ushort transactionId)
    {
        if (frame.Length < 9) throw new FormatException("Frame too short.");
        if (BinaryPrimitives.ReadUInt16BigEndian(frame) != transactionId) throw new FormatException("Transaction id mismatch.");
        if ((frame[7] & 0x80) != 0) throw new InvalidOperationException($"Modbus exception code {frame[8]}.");
        int bytes = frame[8];
        var regs = new ushort[bytes / 2];
        for (int i = 0; i < regs.Length; i++) regs[i] = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(9 + 2 * i, 2));
        return regs;
    }

    /// <summary>Decodes registers starting at <paramref name="baseAddress"/> according to a register map.</summary>
    public static IReadOnlyList<(SensorKind Kind, double Value)> Decode(ReadOnlySpan<ushort> regs, ushort baseAddress, IEnumerable<ModbusRegisterMap> map)
    {
        var list = new List<(SensorKind, double)>();
        foreach (var m in map)
        {
            int i = m.Address - baseAddress;
            if (i < 0 || i + m.Registers > regs.Length) continue;
            uint hi = regs[i], lo = m.Registers == 2 ? regs[i + 1] : 0u;
            if (m.WordSwap) (hi, lo) = (lo, hi);
            uint u32 = hi << 16 | lo;
            double raw = m.Type switch
            {
                RegisterType.UInt16 => regs[i],
                RegisterType.Int16 => (short)regs[i],
                RegisterType.UInt32 => u32,
                RegisterType.Int32 => (int)u32,
                RegisterType.Float32 => BitConverter.UInt32BitsToSingle(u32),
                _ => double.NaN,
            };
            list.Add((m.Kind, raw * m.Scale + m.Offset));
        }
        return list;
    }

    /// <summary>A typical multi-parameter water-quality probe map (DO, temperature, pH, ORP, salinity, turbidity as scaled UInt16).</summary>
    public static IReadOnlyList<ModbusRegisterMap> TypicalProbeMap { get; } =
    [
        new(SensorKind.DissolvedOxygen, 0, Scale: 0.01),
        new(SensorKind.Temperature, 1, Scale: 0.1),
        new(SensorKind.Ph, 2, Scale: 0.01),
        new(SensorKind.Orp, 3, RegisterType.Int16),
        new(SensorKind.Salinity, 4, Scale: 0.1),
        new(SensorKind.Turbidity, 5, Scale: 0.1),
    ];
}

/// <summary>Minimal Modbus TCP client (function 0x03) for PLCs and probe gateways.</summary>
public sealed class ModbusTcpClient(string host, int port = 502, byte unit = 1) : IAsyncDisposable
{
    private TcpClient? _tcp;
    private ushort _tid;

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort count, CancellationToken ct = default)
    {
        if (_tcp is not { Connected: true }) { _tcp?.Dispose(); _tcp = new TcpClient(); await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false); }
        var s = _tcp.GetStream();
        ushort tid = ++_tid;
        await s.WriteAsync(Modbus.ReadHoldingRegistersTcp(tid, unit, address, count), ct).ConfigureAwait(false);
        var header = new byte[9];
        await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if ((header[7] & 0x80) != 0) throw new InvalidOperationException($"Modbus exception code {header[8]}.");
        var body = new byte[header[8]];
        await s.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return Modbus.ParseReadResponseTcp([.. header, .. body], tid);
    }

    public async Task<IReadOnlyList<SensorReading>> ReadProbeAsync(string deviceId, string pondId, IReadOnlyList<ModbusRegisterMap> map, CancellationToken ct = default)
    {
        ushort start = map.Min(m => m.Address), end = (ushort)map.Max(m => m.Address + m.Registers);
        var regs = await ReadHoldingRegistersAsync(start, (ushort)(end - start), ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        return Modbus.Decode(regs, start, map).Select(x => new SensorReading(deviceId, pondId, x.Kind, x.Value, now, SensorReading.DefaultUnit(x.Kind))).ToList();
    }

    public ValueTask DisposeAsync() { _tcp?.Dispose(); return ValueTask.CompletedTask; }
}
