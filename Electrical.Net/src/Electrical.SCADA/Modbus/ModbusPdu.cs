using System.Buffers.Binary;

namespace Electrical.SCADA.Modbus;

/// <summary>Modbus public function codes supported by the codec (Modbus Application Protocol v1.1b3).</summary>
public enum ModbusFunction : byte
{
    /// <summary>FC 1: read 1–2000 coils (read/write bits).</summary>
    ReadCoils = 1,
    /// <summary>FC 2: read 1–2000 discrete inputs (read-only bits).</summary>
    ReadDiscreteInputs = 2,
    /// <summary>FC 3: read 1–125 holding registers.</summary>
    ReadHoldingRegisters = 3,
    /// <summary>FC 4: read 1–125 input registers.</summary>
    ReadInputRegisters = 4,
    /// <summary>FC 5: write a single coil (0xFF00 = ON, 0x0000 = OFF).</summary>
    WriteSingleCoil = 5,
    /// <summary>FC 6: write a single holding register.</summary>
    WriteSingleRegister = 6,
    /// <summary>FC 15: write 1–1968 coils.</summary>
    WriteMultipleCoils = 15,
    /// <summary>FC 16: write 1–123 holding registers.</summary>
    WriteMultipleRegisters = 16,
}

/// <summary>Modbus exception codes returned in an exception response (function code | 0x80).</summary>
public enum ModbusExceptionCode : byte
{
    None = 0,
    IllegalFunction = 1,
    IllegalDataAddress = 2,
    IllegalDataValue = 3,
    ServerDeviceFailure = 4,
    Acknowledge = 5,
    ServerDeviceBusy = 6,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetFailedToRespond = 0x0B,
}

/// <summary>The four Modbus data tables.</summary>
public enum ModbusTable
{
    /// <summary>Read/write bits (0xxxx).</summary>
    Coil,
    /// <summary>Read-only bits (1xxxx).</summary>
    DiscreteInput,
    /// <summary>Read-only 16-bit registers (3xxxx).</summary>
    InputRegister,
    /// <summary>Read/write 16-bit registers (4xxxx).</summary>
    HoldingRegister,
}

/// <summary>Thrown by the client when the server answers with an exception response or the frame is malformed.</summary>
public sealed class ModbusException(ModbusFunction function, ModbusExceptionCode code, string? message = null)
    : Exception(message ?? $"Modbus exception {code} ({(byte)code}) for {function}.")
{
    public ModbusFunction Function { get; } = function;
    public ModbusExceptionCode Code { get; } = code;
}

/// <summary>
/// A Modbus request PDU. <see cref="Address"/> is the zero-based protocol address; <see cref="Quantity"/> the number of bits/registers.
/// For write requests the payload is in <see cref="Registers"/> (FC 6/16) or <see cref="Bits"/> (FC 5/15).
/// </summary>
public sealed record ModbusRequest(ModbusFunction Function, ushort Address, ushort Quantity, ushort[]? Registers = null, bool[]? Bits = null)
{
    public static ModbusRequest ReadCoils(ushort address, ushort quantity) => new(ModbusFunction.ReadCoils, address, quantity);
    public static ModbusRequest ReadDiscreteInputs(ushort address, ushort quantity) => new(ModbusFunction.ReadDiscreteInputs, address, quantity);
    public static ModbusRequest ReadHoldingRegisters(ushort address, ushort quantity) => new(ModbusFunction.ReadHoldingRegisters, address, quantity);
    public static ModbusRequest ReadInputRegisters(ushort address, ushort quantity) => new(ModbusFunction.ReadInputRegisters, address, quantity);
    public static ModbusRequest WriteSingleCoil(ushort address, bool value) => new(ModbusFunction.WriteSingleCoil, address, 1, Bits: [value]);
    public static ModbusRequest WriteSingleRegister(ushort address, ushort value) => new(ModbusFunction.WriteSingleRegister, address, 1, [value]);
    public static ModbusRequest WriteMultipleCoils(ushort address, bool[] values) => new(ModbusFunction.WriteMultipleCoils, address, (ushort)values.Length, Bits: values);
    public static ModbusRequest WriteMultipleRegisters(ushort address, ushort[] values) => new(ModbusFunction.WriteMultipleRegisters, address, (ushort)values.Length, values);
}

/// <summary>
/// A Modbus response PDU. Read responses carry <see cref="Registers"/> or <see cref="Bits"/>; write responses echo address/quantity
/// (and the value for FC 5/6). An exception response has <see cref="Exception"/> ≠ <see cref="ModbusExceptionCode.None"/>.
/// </summary>
public sealed record ModbusResponse(ModbusFunction Function, ushort Address = 0, ushort Quantity = 0, ushort[]? Registers = null, bool[]? Bits = null,
    ModbusExceptionCode Exception = ModbusExceptionCode.None)
{
    public bool IsException => Exception != ModbusExceptionCode.None;
    public static ModbusResponse Error(ModbusFunction function, ModbusExceptionCode code) => new(function, Exception: code);
}

/// <summary>Encoder/decoder for Modbus PDUs (function code + data, big-endian), independent of the transport.</summary>
public static class ModbusPdu
{
    /// <summary>Largest PDU allowed by the specification [bytes].</summary>
    public const int MaxPduLength = 253;

    public static bool IsSupported(byte functionCode) => functionCode is 1 or 2 or 3 or 4 or 5 or 6 or 15 or 16;

    /// <summary>Encodes a request PDU. Validates quantity limits (throws <see cref="ArgumentException"/>).</summary>
    public static byte[] EncodeRequest(ModbusRequest r)
    {
        switch (r.Function)
        {
            case ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs or ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters:
            {
                int max = r.Function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs ? 2000 : 125;
                if (r.Quantity < 1 || r.Quantity > max) throw new ArgumentException($"Quantity must be 1..{max}.");
                var b = new byte[5];
                b[0] = (byte)r.Function;
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), r.Address);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), r.Quantity);
                return b;
            }
            case ModbusFunction.WriteSingleCoil:
            {
                var b = new byte[5];
                b[0] = 5;
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), r.Address);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), r.Bits is [true, ..] ? (ushort)0xFF00 : (ushort)0);
                return b;
            }
            case ModbusFunction.WriteSingleRegister:
            {
                var b = new byte[5];
                b[0] = 6;
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), r.Address);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), r.Registers is { Length: > 0 } v ? v[0] : (ushort)0);
                return b;
            }
            case ModbusFunction.WriteMultipleCoils:
            {
                var bits = r.Bits ?? throw new ArgumentException("Bits required.");
                if (bits.Length < 1 || bits.Length > 1968) throw new ArgumentException("Quantity must be 1..1968.");
                int n = (bits.Length + 7) / 8;
                var b = new byte[6 + n];
                b[0] = 15;
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), r.Address);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), (ushort)bits.Length);
                b[5] = (byte)n;
                PackBits(bits, b.AsSpan(6));
                return b;
            }
            case ModbusFunction.WriteMultipleRegisters:
            {
                var regs = r.Registers ?? throw new ArgumentException("Registers required.");
                if (regs.Length < 1 || regs.Length > 123) throw new ArgumentException("Quantity must be 1..123.");
                var b = new byte[6 + 2 * regs.Length];
                b[0] = 16;
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), r.Address);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), (ushort)regs.Length);
                b[5] = (byte)(2 * regs.Length);
                for (int i = 0; i < regs.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6 + 2 * i), regs[i]);
                return b;
            }
            default:
                throw new ArgumentException($"Unsupported function {r.Function}.");
        }
    }

    /// <summary>
    /// Decodes a request PDU (server side). Throws <see cref="ModbusException"/> with <see cref="ModbusExceptionCode.IllegalFunction"/>
    /// for unsupported functions and <see cref="ModbusExceptionCode.IllegalDataValue"/> for malformed data.
    /// </summary>
    public static ModbusRequest DecodeRequest(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 1) throw new ModbusException(0, ModbusExceptionCode.IllegalFunction, "Empty PDU.");
        var fc = (ModbusFunction)pdu[0];
        if (!IsSupported(pdu[0])) throw new ModbusException(fc, ModbusExceptionCode.IllegalFunction);
        if (pdu.Length < 5) throw new ModbusException(fc, ModbusExceptionCode.IllegalDataValue, "PDU too short.");
        ushort addr = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        ushort qty = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        switch (fc)
        {
            case ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs:
                if (qty is < 1 or > 2000) throw new ModbusException(fc, ModbusExceptionCode.IllegalDataValue);
                return new(fc, addr, qty);
            case ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters:
                if (qty is < 1 or > 125) throw new ModbusException(fc, ModbusExceptionCode.IllegalDataValue);
                return new(fc, addr, qty);
            case ModbusFunction.WriteSingleCoil:
                if (qty is not (0xFF00 or 0)) throw new ModbusException(fc, ModbusExceptionCode.IllegalDataValue);
                return new(fc, addr, 1, Bits: [qty == 0xFF00]);
            case ModbusFunction.WriteSingleRegister:
                return new(fc, addr, 1, [qty]);
            case ModbusFunction.WriteMultipleCoils:
            {
                if (qty is < 1 or > 1968 || pdu.Length < 6 || pdu[5] != (qty + 7) / 8 || pdu.Length < 6 + pdu[5])
                    throw new ModbusException(fc, ModbusExceptionCode.IllegalDataValue);
                return new(fc, addr, qty, Bits: UnpackBits(pdu.Slice(6, pdu[5]), qty));
            }
            default: // WriteMultipleRegisters
            {
                if (qty is < 1 or > 123 || pdu.Length < 6 || pdu[5] != 2 * qty || pdu.Length < 6 + pdu[5])
                    throw new ModbusException(fc, ModbusExceptionCode.IllegalDataValue);
                var regs = new ushort[qty];
                for (int i = 0; i < qty; i++) regs[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + 2 * i)..]);
                return new(fc, addr, qty, regs);
            }
        }
    }

    /// <summary>Encodes a response PDU (server side), including exception responses (FC | 0x80, code).</summary>
    public static byte[] EncodeResponse(ModbusResponse r)
    {
        if (r.IsException) return [(byte)((byte)r.Function | 0x80), (byte)r.Exception];
        switch (r.Function)
        {
            case ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs:
            {
                var bits = r.Bits ?? [];
                int n = (bits.Length + 7) / 8;
                var b = new byte[2 + n];
                b[0] = (byte)r.Function; b[1] = (byte)n;
                PackBits(bits, b.AsSpan(2));
                return b;
            }
            case ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters:
            {
                var regs = r.Registers ?? [];
                var b = new byte[2 + 2 * regs.Length];
                b[0] = (byte)r.Function; b[1] = (byte)(2 * regs.Length);
                for (int i = 0; i < regs.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2 + 2 * i), regs[i]);
                return b;
            }
            case ModbusFunction.WriteSingleCoil:
                return EncodeRequest(ModbusRequest.WriteSingleCoil(r.Address, r.Bits is [true, ..]));
            case ModbusFunction.WriteSingleRegister:
                return EncodeRequest(ModbusRequest.WriteSingleRegister(r.Address, r.Registers is { Length: > 0 } v ? v[0] : (ushort)0));
            default:
            {
                var b = new byte[5];
                b[0] = (byte)r.Function;
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), r.Address);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(3), r.Quantity);
                return b;
            }
        }
    }

    /// <summary>
    /// Decodes a response PDU (client side). <paramref name="request"/> supplies the expected function and the bit count for
    /// FC 1/2 (the byte count is padded to whole bytes). Exception responses are returned with <see cref="ModbusResponse.Exception"/> set.
    /// </summary>
    public static ModbusResponse DecodeResponse(ReadOnlySpan<byte> pdu, ModbusRequest request)
    {
        if (pdu.Length < 2) throw new ModbusException(request.Function, ModbusExceptionCode.ServerDeviceFailure, "Response too short.");
        if ((pdu[0] & 0x80) != 0)
        {
            if ((pdu[0] & 0x7F) != (byte)request.Function) throw new ModbusException(request.Function, ModbusExceptionCode.ServerDeviceFailure, "Function mismatch.");
            return ModbusResponse.Error(request.Function, (ModbusExceptionCode)pdu[1]);
        }
        if (pdu[0] != (byte)request.Function) throw new ModbusException(request.Function, ModbusExceptionCode.ServerDeviceFailure, "Function mismatch.");
        switch (request.Function)
        {
            case ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs:
            {
                int n = pdu[1];
                if (pdu.Length < 2 + n || n < (request.Quantity + 7) / 8) throw new ModbusException(request.Function, ModbusExceptionCode.ServerDeviceFailure, "Byte count mismatch.");
                return new(request.Function, request.Address, request.Quantity, Bits: UnpackBits(pdu.Slice(2, n), request.Quantity));
            }
            case ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters:
            {
                int n = pdu[1];
                if (pdu.Length < 2 + n || n != 2 * request.Quantity) throw new ModbusException(request.Function, ModbusExceptionCode.ServerDeviceFailure, "Byte count mismatch.");
                var regs = new ushort[n / 2];
                for (int i = 0; i < regs.Length; i++) regs[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(2 + 2 * i)..]);
                return new(request.Function, request.Address, request.Quantity, regs);
            }
            default:
            {
                if (pdu.Length < 5) throw new ModbusException(request.Function, ModbusExceptionCode.ServerDeviceFailure, "Response too short.");
                ushort a = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]), v = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
                return request.Function switch
                {
                    ModbusFunction.WriteSingleCoil => new(request.Function, a, 1, Bits: [v == 0xFF00]),
                    ModbusFunction.WriteSingleRegister => new(request.Function, a, 1, [v]),
                    _ => new(request.Function, a, v),
                };
            }
        }
    }

    /// <summary>Packs bits LSB-first into bytes (Modbus coil order).</summary>
    public static void PackBits(ReadOnlySpan<bool> bits, Span<byte> dest)
    {
        dest[..((bits.Length + 7) / 8)].Clear();
        for (int i = 0; i < bits.Length; i++) if (bits[i]) dest[i >> 3] |= (byte)(1 << (i & 7));
    }

    /// <summary>Unpacks <paramref name="count"/> bits LSB-first.</summary>
    public static bool[] UnpackBits(ReadOnlySpan<byte> src, int count)
    {
        var bits = new bool[count];
        for (int i = 0; i < count; i++) bits[i] = (src[i >> 3] & (1 << (i & 7))) != 0;
        return bits;
    }
}

/// <summary>
/// Modbus TCP application data unit: MBAP header (transaction id, protocol id = 0, length, unit id) followed by the PDU.
/// </summary>
public static class ModbusTcpAdu
{
    /// <summary>MBAP header length [bytes] (including the unit identifier).</summary>
    public const int HeaderLength = 7;

    public static byte[] Encode(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        var b = new byte[HeaderLength + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)(pdu.Length + 1));
        b[6] = unitId;
        pdu.CopyTo(b.AsSpan(HeaderLength));
        return b;
    }

    /// <summary>Parses an MBAP header; returns the number of PDU bytes that follow it.</summary>
    public static int ParseHeader(ReadOnlySpan<byte> header, out ushort transactionId, out byte unitId)
    {
        if (header.Length < HeaderLength) throw new ArgumentException("MBAP header is 7 bytes.");
        transactionId = BinaryPrimitives.ReadUInt16BigEndian(header);
        ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        int len = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        unitId = header[6];
        if (protocol != 0) throw new InvalidDataException($"Unsupported Modbus protocol id {protocol}.");
        if (len < 2 || len > ModbusPdu.MaxPduLength + 1) throw new InvalidDataException($"Invalid MBAP length {len}.");
        return len - 1;
    }

    /// <summary>Decodes a complete ADU.</summary>
    public static (ushort TransactionId, byte UnitId, byte[] Pdu) Decode(ReadOnlySpan<byte> adu)
    {
        int n = ParseHeader(adu, out var tx, out var unit);
        if (adu.Length < HeaderLength + n) throw new InvalidDataException("Truncated ADU.");
        return (tx, unit, adu.Slice(HeaderLength, n).ToArray());
    }
}
