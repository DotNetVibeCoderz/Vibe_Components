using Electrical.SCADA.Modbus;
using Electrical.PowerFlow;

namespace Electrical.SCADA;

/// <summary>Register layout and measurement noise of an <see cref="RtuSimulator"/>.</summary>
public sealed record RtuOptions
{
    /// <summary>Modbus unit id of the RTU.</summary>
    public byte UnitId { get; init; } = 1;
    /// <summary>First input register of the bus block. Each bus uses 6 registers: Vm [pu], Va [deg], V [kV] as Float32.</summary>
    public ushort BusBaseAddress { get; init; }
    /// <summary>First input register of the branch block. Each branch uses 10 registers: P_from [MW], Q_from [Mvar], P_to [MW], Q_to [Mvar], loading [%] as Float32.</summary>
    public ushort BranchBaseAddress { get; init; } = 2000;
    /// <summary>First input register of the generator block. Each generator uses 4 registers: P [MW], Q [Mvar] as Float32.</summary>
    public ushort GeneratorBaseAddress { get; init; } = 6000;
    /// <summary>Standard deviation of voltage-magnitude noise [pu].</summary>
    public double VoltageNoisePu { get; init; } = 0.001;
    /// <summary>Standard deviation of angle noise [degrees].</summary>
    public double AngleNoiseDeg { get; init; } = 0.02;
    /// <summary>Standard deviation of active/reactive power noise [MW / Mvar].</summary>
    public double PowerNoiseMw { get; init; } = 0.05;
    /// <summary>Seed of the deterministic noise generator.</summary>
    public int Seed { get; init; } = 2026;
}

/// <summary>One mapped measurement point of the RTU.</summary>
/// <param name="Name">Point name, e.g. "BUS4.VM" or "BR3.PF".</param>
/// <param name="Table">Modbus table.</param>
/// <param name="Address">Zero-based address of the first register / bit.</param>
/// <param name="DataType">Raw type (Float32 for analogs, Bool for statuses).</param>
/// <param name="Unit">Engineering unit.</param>
/// <param name="TrueValue">Noise-free value from the power-flow result.</param>
public sealed record RtuPoint(string Name, ModbusTable Table, ushort Address, TagDataType DataType, string Unit, double TrueValue);

/// <summary>
/// Remote terminal unit simulator: publishes a solved <see cref="PowerFlowResult"/> as Modbus registers with deterministic Gaussian noise.
/// Analogs are Float32 input registers (high word first), branch in-service statuses are discrete inputs and coils
/// (coil = breaker command/state). Serve <see cref="Map"/> with <see cref="ModbusTcpServer"/>.
/// </summary>
public sealed class RtuSimulator
{
    private readonly List<RtuPoint> _points = [];
    private Random _rng;

    public RtuSimulator(PowerFlowResult result, RtuOptions? options = null, ModbusRegisterMap? map = null)
    {
        Options = options ?? new RtuOptions();
        Map = map ?? new ModbusRegisterMap(coils: 4096, discreteInputs: 4096, inputRegisters: 65535, holdingRegisters: 1024);
        _rng = new Random(Options.Seed);
        Result = result;
        Publish(result);
    }

    public RtuOptions Options { get; }
    public ModbusRegisterMap Map { get; }
    public PowerFlowResult Result { get; private set; }
    /// <summary>Point list (stable order: buses, branches, generators, statuses).</summary>
    public IReadOnlyList<RtuPoint> Points => _points;

    /// <summary>Input-register address of bus <paramref name="busIndex"/>'s first value (Vm).</summary>
    public ushort BusAddress(int busIndex) => (ushort)(Options.BusBaseAddress + 6 * busIndex);
    /// <summary>Input-register address of branch <paramref name="branchIndex"/>'s first value (P_from).</summary>
    public ushort BranchAddress(int branchIndex) => (ushort)(Options.BranchBaseAddress + 10 * branchIndex);
    /// <summary>Input-register address of generator <paramref name="genIndex"/>'s first value (P).</summary>
    public ushort GeneratorAddress(int genIndex) => (ushort)(Options.GeneratorBaseAddress + 4 * genIndex);

    /// <summary>Writes a new operating point into the map (fresh noise draw from the deterministic sequence).</summary>
    public void Publish(PowerFlowResult result)
    {
        Result = result;
        _points.Clear();
        var o = Options;
        if (o.BusBaseAddress + 6 * result.Buses.Count > o.BranchBaseAddress && o.BranchBaseAddress > o.BusBaseAddress ||
            o.BranchBaseAddress + 10 * result.Branches.Count > o.GeneratorBaseAddress && o.GeneratorBaseAddress > o.BranchBaseAddress ||
            o.GeneratorBaseAddress + 4 * result.Generators.Count > Map.Size(ModbusTable.InputRegister))
            throw new ArgumentException("Network too large for the configured RTU register layout; adjust the base addresses in RtuOptions.");
        for (int i = 0; i < result.Buses.Count; i++)
        {
            var b = result.Buses[i];
            double kv = result.Network.GetBus(b.BusId).BaseKv;
            ushort a = BusAddress(i);
            double vm = b.Vm + o.VoltageNoisePu * Gauss();
            Analog($"BUS{b.BusId}.VM", a, "pu", b.Vm, vm);
            Analog($"BUS{b.BusId}.VA", (ushort)(a + 2), "deg", b.VaDeg, b.VaDeg + o.AngleNoiseDeg * Gauss());
            Analog($"BUS{b.BusId}.KV", (ushort)(a + 4), "kV", b.Vm * kv, vm * kv);
        }
        for (int k = 0; k < result.Branches.Count; k++)
        {
            var br = result.Branches[k];
            ushort a = BranchAddress(k);
            string n = $"BR{br.Index}";
            Analog($"{n}.PF", a, "MW", br.PFrom, br.PFrom + o.PowerNoiseMw * Gauss());
            Analog($"{n}.QF", (ushort)(a + 2), "Mvar", br.QFrom, br.QFrom + o.PowerNoiseMw * Gauss());
            Analog($"{n}.PT", (ushort)(a + 4), "MW", br.PTo, br.PTo + o.PowerNoiseMw * Gauss());
            Analog($"{n}.QT", (ushort)(a + 6), "Mvar", br.QTo, br.QTo + o.PowerNoiseMw * Gauss());
            Analog($"{n}.LOAD", (ushort)(a + 8), "%", br.LoadingPercent, br.LoadingPercent);
        }
        for (int g = 0; g < result.Generators.Count; g++)
        {
            var gen = result.Generators[g];
            ushort a = GeneratorAddress(g);
            Analog($"GEN{g}.P", a, "MW", gen.P, gen.P + o.PowerNoiseMw * Gauss());
            Analog($"GEN{g}.Q", (ushort)(a + 2), "Mvar", gen.Q, gen.Q + o.PowerNoiseMw * Gauss());
        }
        for (int k = 0; k < result.Network.Branches.Count && k < Map.Size(ModbusTable.DiscreteInput); k++)
        {
            bool closed = result.Network.Branches[k].InService;
            Map.SetBit(ModbusTable.DiscreteInput, k, closed);
            Map.SetBit(ModbusTable.Coil, k, closed);
            _points.Add(new RtuPoint($"BR{k}.CLOSED", ModbusTable.DiscreteInput, (ushort)k, TagDataType.Bool, "", closed ? 1 : 0));
        }
    }

    /// <summary>Re-draws noise around the current result (e.g. once per scan cycle).</summary>
    public void Refresh() => Publish(Result);

    /// <summary>Restarts the noise sequence (identical values for identical calls after reset).</summary>
    public void ResetNoise() => _rng = new Random(Options.Seed);

    /// <summary>Adds one tag per RTU point to a tag database (names prefixed by <paramref name="prefix"/>).</summary>
    public void RegisterTags(TagDatabase tags, string prefix = "")
    {
        foreach (var p in _points)
            if (!tags.TryGet(prefix + p.Name, out _))
                tags.Add(prefix + p.Name, p.Unit, new TagAddress(Options.UnitId, p.Table, p.Address, p.DataType), description: $"RTU {Options.UnitId} {p.Name}");
    }

    private void Analog(string name, ushort address, string unit, double trueValue, double measured)
    {
        Map.SetFloat(ModbusTable.InputRegister, address, (float)measured);
        _points.Add(new RtuPoint(name, ModbusTable.InputRegister, address, TagDataType.Float32, unit, trueValue));
    }

    /// <summary>Standard normal deviate (Box–Muller) from the seeded generator.</summary>
    private double Gauss()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
