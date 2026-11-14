namespace Electrical.SCADA.Modbus;

/// <summary>Raised after a Modbus client wrote coils or holding registers.</summary>
public sealed record ModbusWriteEventArgs(ModbusTable Table, ushort Address, ushort Quantity);

/// <summary>
/// Thread-safe in-memory Modbus data model (coils, discrete inputs, input and holding registers) that serves requests
/// through <see cref="Process"/>. 32-bit values use big-endian word order (high word at the lower address, "ABCD").
/// </summary>
public sealed class ModbusRegisterMap
{
    private readonly bool[] _coils, _discrete;
    private readonly ushort[] _input, _holding;
    private readonly Lock _gate = new();

    /// <param name="coils">Number of coils.</param>
    /// <param name="discreteInputs">Number of discrete inputs.</param>
    /// <param name="inputRegisters">Number of input registers.</param>
    /// <param name="holdingRegisters">Number of holding registers.</param>
    public ModbusRegisterMap(int coils = 10000, int discreteInputs = 10000, int inputRegisters = 10000, int holdingRegisters = 10000)
    {
        _coils = new bool[coils];
        _discrete = new bool[discreteInputs];
        _input = new ushort[inputRegisters];
        _holding = new ushort[holdingRegisters];
    }

    /// <summary>Raised (outside the lock) after a client write through <see cref="Process"/>.</summary>
    public event Action<ModbusWriteEventArgs>? Written;

    public int Size(ModbusTable table) => table switch
    {
        ModbusTable.Coil => _coils.Length,
        ModbusTable.DiscreteInput => _discrete.Length,
        ModbusTable.InputRegister => _input.Length,
        _ => _holding.Length,
    };

    public bool GetBit(ModbusTable table, int address) { lock (_gate) return Bits(table)[address]; }
    public void SetBit(ModbusTable table, int address, bool value) { lock (_gate) Bits(table)[address] = value; }
    public ushort GetRegister(ModbusTable table, int address) { lock (_gate) return Regs(table)[address]; }
    public void SetRegister(ModbusTable table, int address, ushort value) { lock (_gate) Regs(table)[address] = value; }

    public ushort[] ReadRegisters(ModbusTable table, int address, int count) { lock (_gate) return Regs(table).AsSpan(address, count).ToArray(); }
    public void WriteRegisters(ModbusTable table, int address, ReadOnlySpan<ushort> values) { lock (_gate) values.CopyTo(Regs(table).AsSpan(address)); }
    public bool[] ReadBits(ModbusTable table, int address, int count) { lock (_gate) return Bits(table).AsSpan(address, count).ToArray(); }

    /// <summary>Writes an IEEE-754 float into two consecutive registers (high word first).</summary>
    public void SetFloat(ModbusTable table, int address, float value)
    {
        Span<ushort> w = stackalloc ushort[2];
        ModbusData.FromFloat(value, w);
        WriteRegisters(table, address, w);
    }

    /// <summary>Reads an IEEE-754 float from two consecutive registers (high word first).</summary>
    public float GetFloat(ModbusTable table, int address)
    {
        lock (_gate) return ModbusData.ToFloat(Regs(table)[address], Regs(table)[address + 1]);
    }

    private bool[] Bits(ModbusTable t) => t switch
    {
        ModbusTable.Coil => _coils,
        ModbusTable.DiscreteInput => _discrete,
        _ => throw new ArgumentException($"{t} is a register table."),
    };

    private ushort[] Regs(ModbusTable t) => t switch
    {
        ModbusTable.InputRegister => _input,
        ModbusTable.HoldingRegister => _holding,
        _ => throw new ArgumentException($"{t} is a bit table."),
    };

    /// <summary>Executes a request against the map. Out-of-range addresses give <see cref="ModbusExceptionCode.IllegalDataAddress"/>.</summary>
    public ModbusResponse Process(ModbusRequest r)
    {
        ModbusWriteEventArgs? written = null;
        ModbusResponse resp;
        lock (_gate)
        {
            resp = r.Function switch
            {
                ModbusFunction.ReadCoils => ReadBitsCore(r, _coils),
                ModbusFunction.ReadDiscreteInputs => ReadBitsCore(r, _discrete),
                ModbusFunction.ReadHoldingRegisters => ReadRegsCore(r, _holding),
                ModbusFunction.ReadInputRegisters => ReadRegsCore(r, _input),
                ModbusFunction.WriteSingleCoil or ModbusFunction.WriteMultipleCoils => WriteBitsCore(r, out written),
                ModbusFunction.WriteSingleRegister or ModbusFunction.WriteMultipleRegisters => WriteRegsCore(r, out written),
                _ => ModbusResponse.Error(r.Function, ModbusExceptionCode.IllegalFunction),
            };
        }
        if (written is not null) Written?.Invoke(written);
        return resp;
    }

    private static bool InRange(int len, int address, int qty) => address + qty <= len;

    private static ModbusResponse ReadBitsCore(ModbusRequest r, bool[] src) =>
        InRange(src.Length, r.Address, r.Quantity)
            ? new(r.Function, r.Address, r.Quantity, Bits: src.AsSpan(r.Address, r.Quantity).ToArray())
            : ModbusResponse.Error(r.Function, ModbusExceptionCode.IllegalDataAddress);

    private static ModbusResponse ReadRegsCore(ModbusRequest r, ushort[] src) =>
        InRange(src.Length, r.Address, r.Quantity)
            ? new(r.Function, r.Address, r.Quantity, src.AsSpan(r.Address, r.Quantity).ToArray())
            : ModbusResponse.Error(r.Function, ModbusExceptionCode.IllegalDataAddress);

    private ModbusResponse WriteBitsCore(ModbusRequest r, out ModbusWriteEventArgs? w)
    {
        w = null;
        var bits = r.Bits ?? [];
        if (bits.Length == 0) return ModbusResponse.Error(r.Function, ModbusExceptionCode.IllegalDataValue);
        if (!InRange(_coils.Length, r.Address, bits.Length)) return ModbusResponse.Error(r.Function, ModbusExceptionCode.IllegalDataAddress);
        bits.CopyTo(_coils, r.Address);
        w = new(ModbusTable.Coil, r.Address, (ushort)bits.Length);
        return new(r.Function, r.Address, (ushort)bits.Length, Bits: r.Function == ModbusFunction.WriteSingleCoil ? bits : null);
    }

    private ModbusResponse WriteRegsCore(ModbusRequest r, out ModbusWriteEventArgs? w)
    {
        w = null;
        var regs = r.Registers ?? [];
        if (regs.Length == 0) return ModbusResponse.Error(r.Function, ModbusExceptionCode.IllegalDataValue);
        if (!InRange(_holding.Length, r.Address, regs.Length)) return ModbusResponse.Error(r.Function, ModbusExceptionCode.IllegalDataAddress);
        regs.CopyTo(_holding, r.Address);
        w = new(ModbusTable.HoldingRegister, r.Address, (ushort)regs.Length);
        return new(r.Function, r.Address, (ushort)regs.Length, r.Function == ModbusFunction.WriteSingleRegister ? regs : null);
    }
}

/// <summary>Conversions between 16-bit registers and engineering data types (big-endian word order).</summary>
public static class ModbusData
{
    public static float ToFloat(ushort hi, ushort lo) => BitConverter.UInt32BitsToSingle(((uint)hi << 16) | lo);

    public static void FromFloat(float v, Span<ushort> dest)
    {
        uint u = BitConverter.SingleToUInt32Bits(v);
        dest[0] = (ushort)(u >> 16);
        dest[1] = (ushort)u;
    }

    public static int ToInt32(ushort hi, ushort lo) => (int)(((uint)hi << 16) | lo);
    public static uint ToUInt32(ushort hi, ushort lo) => ((uint)hi << 16) | lo;
}
