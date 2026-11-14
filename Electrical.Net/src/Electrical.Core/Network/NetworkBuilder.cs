using Electrical.Core.Units;

namespace Electrical.Core.Network;

/// <summary>Fluent builder that accepts physical units (kV, Ω, km, MVA) and converts to the per-unit model.</summary>
public sealed class NetworkBuilder
{
    private readonly PowerNetwork _net;

    public NetworkBuilder(string name = "network", double baseMva = 100, double frequencyHz = 50)
    {
        _net = new PowerNetwork { Name = name, BaseMva = baseMva, FrequencyHz = frequencyHz };
    }

    public PowerNetwork Network => _net;

    public NetworkBuilder AddBus(int id, double baseKv, BusType type = BusType.PQ, string? name = null, GeoPoint? location = null,
        double vmin = 0.9, double vmax = 1.1)
    {
        _net.Buses.Add(new Bus { Id = id, BaseKv = baseKv, Type = type, Name = name, Location = location, Vmin = vmin, Vmax = vmax });
        return this;
    }

    private PerUnitBase BaseAt(int busId) => new(_net.BaseMva, _net.GetBus(busId).BaseKv);

    /// <summary>Adds a line from total series impedance in Ω and total shunt capacitance in µF.</summary>
    public NetworkBuilder AddLineOhm(int from, int to, double rOhm, double xOhm, double cMicroF = 0, double rateMva = 0, string? name = null, double lengthKm = 0)
    {
        var pu = BaseAt(from);
        double bS = 2 * Math.PI * _net.FrequencyHz * cMicroF * 1e-6;
        _net.Branches.Add(new Branch
        {
            From = from, To = to, R = pu.OhmToPu(rOhm), X = pu.OhmToPu(xOhm), B = pu.SiemensToPu(bS), RateA = rateMva, Name = name, LengthKm = lengthKm,
        });
        return this;
    }

    /// <summary>Adds a line from per-km parameters (Ω/km, Ω/km, nF/km).</summary>
    public NetworkBuilder AddLine(int from, int to, double lengthKm, double rOhmPerKm, double xOhmPerKm, double cNanoFPerKm = 0,
        double rateMva = 0, string? name = null, BranchKind kind = BranchKind.Line)
    {
        AddLineOhm(from, to, rOhmPerKm * lengthKm, xOhmPerKm * lengthKm, cNanoFPerKm * lengthKm / 1000, rateMva, name, lengthKm);
        _net.Branches[^1].Kind = kind;
        return this;
    }

    /// <summary>Adds a line using a standard type from <see cref="LineLibrary"/>.</summary>
    public NetworkBuilder AddLine(int from, int to, double lengthKm, string standardType, string? name = null)
    {
        var t = LineLibrary.Get(standardType);
        return AddLine(from, to, lengthKm, t.ROhmPerKm, t.XOhmPerKm, t.CNanoFPerKm, t.RateMva(_net.GetBus(from).BaseKv), name, t.Kind);
    }

    /// <summary>Adds a two-winding transformer from nameplate data: rating [MVA], short-circuit voltage uk [%], copper losses [kW], tap ratio.</summary>
    public NetworkBuilder AddTransformer(int hv, int lv, double ratingMva, double ukPercent, double copperLossKw = 0, double tap = 1,
        double shiftDeg = 0, WindingConnection connection = WindingConnection.Dyn, string? name = null)
    {
        double zPu = ukPercent / 100 * _net.BaseMva / ratingMva;
        double rPu = copperLossKw / 1000 / ratingMva * _net.BaseMva / ratingMva;
        double xPu = Math.Sqrt(Math.Max(zPu * zPu - rPu * rPu, 1e-12));
        _net.Branches.Add(new Branch
        {
            From = hv, To = lv, Kind = BranchKind.Transformer, R = rPu, X = xPu, Tap = tap, ShiftDeg = shiftDeg, RateA = ratingMva,
            Connection = connection, Name = name,
        });
        return this;
    }

    public NetworkBuilder AddLoad(int bus, double pMw, double qMvar, string? name = null, string? category = null, ZipModel? model = null)
    {
        _net.Loads.Add(new Load { Bus = bus, P = pMw, Q = qMvar, Name = name, Category = category, Model = model ?? ZipModel.ConstantPower });
        return this;
    }

    public NetworkBuilder AddGenerator(int bus, double pMw, double vset = 1.0, double pmax = 9999, double pmin = 0, double qmax = 9999,
        double qmin = -9999, GeneratorKind kind = GeneratorKind.Thermal, PolynomialCost? cost = null, string? name = null, double mbase = 100)
    {
        var b = _net.GetBus(bus);
        if (b.Type == BusType.PQ) b.Type = BusType.PV;
        b.Vm = vset;
        _net.Generators.Add(new Generator
        {
            Bus = bus, P = pMw, Vset = vset, Pmax = pmax, Pmin = pmin, Qmax = qmax, Qmin = qmin, Kind = kind, Cost = cost ?? default, Name = name, MBase = mbase,
        });
        return this;
    }

    /// <summary>Adds a shunt (capacitor bank: positive Mvar; reactor: negative) at 1 pu voltage.</summary>
    public NetworkBuilder AddShunt(int bus, double mvar, double mw = 0)
    {
        var b = _net.GetBus(bus);
        b.Bs += mvar;
        b.Gs += mw;
        return this;
    }

    /// <summary>Returns the network after validation (throws on structural errors).</summary>
    public PowerNetwork Build(bool validate = true)
    {
        if (validate)
        {
            var issues = _net.Validate();
            if (issues.Count > 0) throw new InvalidOperationException("Invalid network: " + string.Join(" ", issues));
        }
        return _net;
    }
}

/// <summary>Standard conductor/cable type.</summary>
public sealed record LineType(string Name, double ROhmPerKm, double XOhmPerKm, double CNanoFPerKm, double MaxKa, BranchKind Kind)
{
    public double RateMva(double kv) => Math.Sqrt(3) * kv * MaxKa;
}

/// <summary>Library of common overhead conductors and cables (typical 50 Hz values, pandapower-compatible names).</summary>
public static class LineLibrary
{
    private static readonly Dictionary<string, LineType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NAYY 4x50 SE"] = new("NAYY 4x50 SE", 0.642, 0.083, 210, 0.142, BranchKind.Cable),
        ["NAYY 4x150 SE"] = new("NAYY 4x150 SE", 0.208, 0.080, 261, 0.270, BranchKind.Cable),
        ["NA2XS2Y 1x95 RM/25 12/20 kV"] = new("NA2XS2Y 1x95 RM/25 12/20 kV", 0.313, 0.132, 216, 0.252, BranchKind.Cable),
        ["NA2XS2Y 1x240 RM/25 12/20 kV"] = new("NA2XS2Y 1x240 RM/25 12/20 kV", 0.122, 0.112, 304, 0.421, BranchKind.Cable),
        ["94-AL1/15-ST1A 20.0"] = new("94-AL1/15-ST1A 20.0", 0.306, 0.350, 10, 0.350, BranchKind.Line),
        ["149-AL1/24-ST1A 20.0"] = new("149-AL1/24-ST1A 20.0", 0.194, 0.337, 10.5, 0.470, BranchKind.Line),
        ["243-AL1/39-ST1A 110.0"] = new("243-AL1/39-ST1A 110.0", 0.119, 0.390, 9.0, 0.645, BranchKind.Line),
        ["490-AL1/64-ST1A 220.0"] = new("490-AL1/64-ST1A 220.0", 0.059, 0.285, 10.0, 0.960, BranchKind.Line),
        ["ACSR Hawk 477 kcmil"] = new("ACSR Hawk 477 kcmil", 0.1195, 0.380, 9.5, 0.659, BranchKind.Line),
        ["ACSR Drake 795 kcmil"] = new("ACSR Drake 795 kcmil", 0.0718, 0.367, 9.8, 0.907, BranchKind.Line),
        ["AAAC 150 mm2"] = new("AAAC 150 mm2", 0.2253, 0.335, 10.5, 0.425, BranchKind.Line),
    };

    public static IEnumerable<LineType> All => Types.Values;

    public static LineType Get(string name) =>
        Types.TryGetValue(name, out var t) ? t : throw new KeyNotFoundException($"Unknown line type '{name}'. Known: {string.Join(", ", Types.Keys)}");

    public static void Register(LineType type) => Types[type.Name] = type;
}
