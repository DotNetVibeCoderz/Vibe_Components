using System.Numerics;

namespace Electrical.Distribution;

/// <summary>A distribution bus with its nominal line-to-line voltage and the phases present.</summary>
public sealed class DistributionBus
{
    public DistributionBus(string name, double baseKvLL, PhaseSet phases = PhaseSet.ABC)
    {
        Name = name;
        BaseKvLL = baseKvLL;
        Phases = phases;
    }

    public string Name { get; }
    /// <summary>Nominal line-to-line voltage [kV]; per-unit phase voltages use BaseKvLL/√3.</summary>
    public double BaseKvLL { get; set; }
    /// <summary>Phases declared for the bus; the solver also adds every phase connected by a series element.</summary>
    public PhaseSet Phases { get; set; }
    /// <summary>Optional drawing coordinates.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }
    /// <summary>Line-to-neutral base voltage [V].</summary>
    public double BaseVoltsLN => BaseKvLL * 1000 / Math.Sqrt(3);

    public override string ToString() => $"{Name} [{Phases.ToLabel()}, {BaseKvLL} kV]";
}

/// <summary>Kind of series element.</summary>
public enum DistributionBranchKind { Line, Transformer, Regulator }

/// <summary>Line segment (overhead line, cable or switch) with a phase impedance/admittance line code.</summary>
public sealed class LineSegment
{
    public required string Name { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required LineCode Code { get; set; }
    /// <summary>Length [km].</summary>
    public double LengthKm { get; set; }
    /// <summary>Phase index (0 = A …) of each row of the line code, e.g. [2, 1] for a "C B" lateral.</summary>
    public required int[] PhaseOrder { get; init; }
    public bool IsSwitch { get; init; }
    /// <summary>Closed/in service (an open switch or line is removed from the model).</summary>
    public bool Closed { get; set; } = true;
    public PhaseSet Phases => PhaseSetExtensions.FromIndices(PhaseOrder);
    public override string ToString() => $"Line {Name} {From}-{To} [{Phases.ToLabel()}] {LengthKm:0.###} km";
}

/// <summary>Three-phase transformer bank connections (ANSI standard 30° lag of the low side for Δ-Y and Y-Δ).</summary>
public enum TransformerConnection
{
    /// <summary>Grounded wye – grounded wye (also single-phase units connected phase-to-ground).</summary>
    WyeGWyeG,
    /// <summary>Delta – grounded wye (low side lags 30°).</summary>
    DeltaWyeG,
    /// <summary>Ungrounded wye – delta (low side lags 30°).</summary>
    WyeDelta,
    /// <summary>Grounded wye – delta (low side lags 30°).</summary>
    WyeGDelta,
    /// <summary>Delta – delta (0° shift).</summary>
    DeltaDelta,
}

/// <summary>
/// Two-winding transformer bank made of single-phase units. Voltages are line-to-line for three-phase banks and the winding voltage for
/// single-phase units; the impedance is in % on the bank rating.
/// </summary>
public sealed class DistributionTransformer
{
    public required string Name { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public TransformerConnection Connection { get; set; } = TransformerConnection.WyeGWyeG;
    /// <summary>Bank rating [kVA] (all phases).</summary>
    public required double KvaRating { get; set; }
    /// <summary>High-side (winding 1) rated voltage [kV]: line-to-line for 3-phase banks, winding voltage for single-phase units.</summary>
    public required double KvPrimary { get; set; }
    /// <summary>Low-side (winding 2) rated voltage [kV], same convention as <see cref="KvPrimary"/>.</summary>
    public required double KvSecondary { get; set; }
    /// <summary>Total series resistance [% on rating].</summary>
    public double RPercent { get; set; } = 1;
    /// <summary>Leakage reactance [% on rating].</summary>
    public double XPercent { get; set; } = 6;
    /// <summary>Winding-1 tap [pu].</summary>
    public double TapPrimary { get; set; } = 1;
    /// <summary>Winding-2 tap [pu]; raising it raises the secondary voltage (OLTC acts on this value).</summary>
    public double TapSecondary { get; set; } = 1;
    /// <summary>Phases (ABC for banks; a single phase for a single-phase unit, which must use <see cref="TransformerConnection.WyeGWyeG"/>).</summary>
    public PhaseSet Phases { get; init; } = PhaseSet.ABC;
    public bool InService { get; set; } = true;
    public override string ToString() => $"Transformer {Name} {From}-{To} {Connection} {KvaRating} kVA {KvPrimary}/{KvSecondary} kV";
}

/// <summary>
/// Wye-connected step-voltage regulator: one single-phase unit per phase with output/input ratio 1 + step·tap
/// (IEEE test feeder convention, 32 steps of 0.625 % = ±10 %).
/// </summary>
public sealed class VoltageRegulator
{
    public required string Name { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public PhaseSet Phases { get; init; } = PhaseSet.ABC;
    /// <summary>Tap position per phase index 0..2 (ignored for absent phases), −MaxTap … +MaxTap.</summary>
    public int[] Taps { get; } = new int[3];
    /// <summary>Voltage change per step [%].</summary>
    public double StepPercent { get; set; } = 0.625;
    public int MaxTap { get; set; } = 16;
    /// <summary>Rating per phase [kVA] (used for the leakage impedance only).</summary>
    public double KvaPerPhase { get; set; } = 1666;
    /// <summary>Rated phase-to-neutral voltage [kV].</summary>
    public required double KvLN { get; set; }
    /// <summary>Series impedance [% on the unit rating]; small values model an ideal regulator.</summary>
    public double RPercent { get; set; } = 0.0001;
    public double XPercent { get; set; } = 0.01;

    /// <summary>Output/input voltage ratio of phase <paramref name="phase"/>.</summary>
    public double Ratio(int phase) => 1 + StepPercent / 100 * Taps[phase];

    /// <summary>Sets the tap of a phase, clamped to ±MaxTap.</summary>
    public void SetTap(int phase, int tap) => Taps[phase] = Math.Clamp(tap, -MaxTap, MaxTap);

    public override string ToString() => $"Regulator {Name} {From}-{To} [{Phases.ToLabel()}] taps {string.Join("/", Phases.Indices().Select(p => Taps[p]))}";
}

/// <summary>Connection of loads and capacitors.</summary>
public enum LoadConnection { Wye, Delta }

/// <summary>Voltage dependence of a load (Kersting / IEEE test feeder codes PQ, Z, I; OpenDSS models 1, 2, 5).</summary>
public enum LoadModel { ConstantPower, ConstantImpedance, ConstantCurrent }

/// <summary>
/// Spot load. <see cref="PowerKva"/> holds three complex powers [kW + j·kvar]: for wye loads per phase a, b, c (phase-to-neutral); for delta loads
/// per phase pair ab, bc, ca.
/// </summary>
public sealed class DistributionLoad
{
    public required string Name { get; init; }
    public required string Bus { get; init; }
    public LoadConnection Connection { get; set; } = LoadConnection.Wye;
    public LoadModel Model { get; set; } = LoadModel.ConstantPower;
    /// <summary>Nominal power per element [kW + j·kvar] at <see cref="NominalKv"/>.</summary>
    public Complex[] PowerKva { get; } = new Complex[3];
    /// <summary>Rated voltage of each element [kV] (phase-to-neutral for wye, line-to-line for delta); NaN = bus nominal.</summary>
    public double NominalKv { get; set; } = double.NaN;
    public bool InService { get; set; } = true;

    public Complex TotalKva => PowerKva[0] + PowerKva[1] + PowerKva[2];
    public override string ToString() => $"Load {Name} @{Bus} {Connection}-{Model} {TotalKva.Real:0.#} kW {TotalKva.Imaginary:0.#} kvar";
}

/// <summary>Shunt capacitor bank; <see cref="Kvar"/> per element (phase for wye, phase pair ab/bc/ca for delta) at <see cref="RatedKv"/>.</summary>
public sealed class ShuntCapacitor
{
    public required string Name { get; init; }
    public required string Bus { get; init; }
    public LoadConnection Connection { get; set; } = LoadConnection.Wye;
    /// <summary>Rating per element [kvar].</summary>
    public double[] Kvar { get; } = new double[3];
    /// <summary>Element rated voltage [kV] (phase-to-neutral for wye, line-to-line for delta); NaN = bus nominal.</summary>
    public double RatedKv { get; set; } = double.NaN;
    /// <summary>Switch state (all phases).</summary>
    public bool IsOn { get; set; } = true;
    public double TotalKvar => Kvar.Sum();
    public override string ToString() => $"Capacitor {Name} @{Bus} {TotalKvar} kvar {(IsOn ? "on" : "off")}";
}

/// <summary>Substation source: an ideal balanced three-phase voltage at <see cref="Bus"/>.</summary>
public sealed class DistributionSource
{
    public required string Bus { get; set; }
    /// <summary>Voltage magnitude [pu of the bus base].</summary>
    public double VoltagePu { get; set; } = 1;
    /// <summary>Angle of phase A [degrees]; B and C follow at −120° and +120°.</summary>
    public double AngleDeg { get; set; }
}

/// <summary>
/// Three-phase (phase-frame) distribution network: buses with phase sets, line segments with phase impedance matrices, transformer banks,
/// step-voltage regulators, wye/delta loads, shunt capacitors, a substation source and controllers.
/// Units: kV, kVA, kW, kvar, km, Ω, S.
/// </summary>
public sealed class DistributionNetwork
{
    private readonly Dictionary<string, DistributionBus> _buses = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; set; } = "feeder";
    /// <summary>System frequency [Hz].</summary>
    public double FrequencyHz { get; set; } = 60;

    public List<DistributionBus> Buses { get; } = [];
    public List<LineSegment> Lines { get; } = [];
    public List<DistributionTransformer> Transformers { get; } = [];
    public List<VoltageRegulator> Regulators { get; } = [];
    public List<DistributionLoad> Loads { get; } = [];
    public List<ShuntCapacitor> Capacitors { get; } = [];
    /// <summary>Controllers acting during static control solutions and time-series runs.</summary>
    public List<IDistributionController> Controllers { get; } = [];
    /// <summary>Line codes by name (informational; segments hold a reference to their code).</summary>
    public Dictionary<string, LineCode> LineCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Messages produced by importers (unsupported objects etc.).</summary>
    public List<string> Warnings { get; } = [];

    public DistributionSource? Source { get; set; }

    /// <summary>Adds a bus (or returns the existing one with the same name).</summary>
    public DistributionBus AddBus(string name, double baseKvLL, PhaseSet phases = PhaseSet.ABC)
    {
        if (_buses.TryGetValue(name, out var b)) return b;
        b = new DistributionBus(name, baseKvLL, phases);
        Buses.Add(b);
        _buses[name] = b;
        return b;
    }

    public bool TryGetBus(string name, out DistributionBus bus)
    {
        if (_buses.Count != Buses.Count) Reindex();
        return _buses.TryGetValue(name, out bus!);
    }

    public DistributionBus Bus(string name) =>
        TryGetBus(name, out var b) ? b : throw new KeyNotFoundException($"Bus '{name}' not found.");

    private void Reindex()
    {
        _buses.Clear();
        foreach (var b in Buses) _buses[b.Name] = b;
    }

    /// <summary>Sets the substation source.</summary>
    public DistributionSource SetSource(string bus, double voltagePu = 1, double angleDeg = 0)
    {
        Bus(bus);
        Source = new DistributionSource { Bus = bus, VoltagePu = voltagePu, AngleDeg = angleDeg };
        return Source;
    }

    /// <summary>Adds a line segment of <paramref name="length"/> in <paramref name="unit"/>. Phases default to the first n of A, B, C.</summary>
    public LineSegment AddLine(string name, string from, string to, LineCode code, double length, LengthUnit unit = LengthUnit.Kilometer,
        int[]? phaseOrder = null)
    {
        phaseOrder ??= Enumerable.Range(0, code.PhaseCount).ToArray();
        if (phaseOrder.Length != code.PhaseCount)
            throw new ArgumentException($"Line {name}: {phaseOrder.Length} phases but line code {code.Name} has {code.PhaseCount}.");
        Bus(from); Bus(to);
        var l = new LineSegment { Name = name, From = from, To = to, Code = code, LengthKm = Lengths.ToKm(length, unit), PhaseOrder = phaseOrder };
        Lines.Add(l);
        LineCodes.TryAdd(code.Name, code);
        return l;
    }

    /// <summary>Adds a line using phase letters, e.g. <c>"cb"</c>.</summary>
    public LineSegment AddLine(string name, string from, string to, LineCode code, double length, LengthUnit unit, string phases) =>
        AddLine(name, from, to, code, length, unit, phases.Select(ch => char.ToLowerInvariant(ch) - 'a').ToArray());

    /// <summary>Adds a (nearly ideal) switch: 1·10⁻⁴ Ω per phase.</summary>
    public LineSegment AddSwitch(string name, string from, string to, bool closed = true, PhaseSet phases = PhaseSet.ABC)
    {
        int n = phases.Count();
        var z = new Complex[n, n];
        for (int i = 0; i < n; i++) z[i, i] = new Complex(1e-4, 0);
        var l = new LineSegment
        {
            Name = name, From = from, To = to, Code = new LineCode("switch", z), LengthKm = 1, PhaseOrder = phases.Indices(), IsSwitch = true,
            Closed = closed,
        };
        Bus(from); Bus(to);
        Lines.Add(l);
        return l;
    }

    public DistributionTransformer AddTransformer(string name, string from, string to, TransformerConnection connection, double kva,
        double kvPrimary, double kvSecondary, double rPercent, double xPercent, PhaseSet phases = PhaseSet.ABC)
    {
        Bus(from); Bus(to);
        var t = new DistributionTransformer
        {
            Name = name, From = from, To = to, Connection = connection, KvaRating = kva, KvPrimary = kvPrimary, KvSecondary = kvSecondary,
            RPercent = rPercent, XPercent = xPercent, Phases = phases,
        };
        Transformers.Add(t);
        return t;
    }

    public VoltageRegulator AddRegulator(string name, string from, string to, double kvLN, PhaseSet phases = PhaseSet.ABC, params int[] taps)
    {
        Bus(from); Bus(to);
        var r = new VoltageRegulator { Name = name, From = from, To = to, KvLN = kvLN, Phases = phases };
        var idx = phases.Indices();
        for (int i = 0; i < Math.Min(taps.Length, idx.Length); i++) r.SetTap(idx[i], taps[i]);
        Regulators.Add(r);
        return r;
    }

    /// <summary>Adds a wye load with per-phase powers [kW, kvar] (zero for unused phases).</summary>
    public DistributionLoad AddWyeLoad(string name, string bus, LoadModel model, Complex a, Complex b, Complex c) =>
        AddLoad(name, bus, LoadConnection.Wye, model, a, b, c);

    /// <summary>Adds a delta load with per-pair powers ab, bc, ca [kW, kvar].</summary>
    public DistributionLoad AddDeltaLoad(string name, string bus, LoadModel model, Complex ab, Complex bc, Complex ca) =>
        AddLoad(name, bus, LoadConnection.Delta, model, ab, bc, ca);

    private DistributionLoad AddLoad(string name, string bus, LoadConnection conn, LoadModel model, Complex s0, Complex s1, Complex s2)
    {
        Bus(bus);
        var l = new DistributionLoad { Name = name, Bus = bus, Connection = conn, Model = model };
        l.PowerKva[0] = s0; l.PowerKva[1] = s1; l.PowerKva[2] = s2;
        Loads.Add(l);
        return l;
    }

    /// <summary>Adds a capacitor bank with per-element ratings [kvar].</summary>
    public ShuntCapacitor AddCapacitor(string name, string bus, double kvarA, double kvarB, double kvarC, LoadConnection connection = LoadConnection.Wye,
        double ratedKv = double.NaN)
    {
        Bus(bus);
        var c = new ShuntCapacitor { Name = name, Bus = bus, Connection = connection, RatedKv = ratedKv };
        c.Kvar[0] = kvarA; c.Kvar[1] = kvarB; c.Kvar[2] = kvarC;
        Capacitors.Add(c);
        return c;
    }

    public LineSegment Line(string name) => Lines.First(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
    public VoltageRegulator Regulator(string name) => Regulators.First(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
    public DistributionTransformer Transformer(string name) => Transformers.First(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
    public ShuntCapacitor Capacitor(string name) => Capacitors.First(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
    public DistributionLoad Load(string name) => Loads.First(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Total nominal load [kW + j·kvar].</summary>
    public Complex TotalLoadKva => Loads.Where(l => l.InService).Aggregate(Complex.Zero, (s, l) => s + l.TotalKva);

    /// <summary>Checks references and data consistency.</summary>
    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (Source is null) e.Add("No source defined.");
        else if (!TryGetBus(Source.Bus, out _)) e.Add($"Source bus '{Source.Bus}' not found.");
        void Check(string what, string bus) { if (!TryGetBus(bus, out _)) e.Add($"{what}: bus '{bus}' not found."); }
        foreach (var l in Lines) { Check($"Line {l.Name}", l.From); Check($"Line {l.Name}", l.To); if (l.LengthKm < 0) e.Add($"Line {l.Name}: negative length."); }
        foreach (var t in Transformers)
        {
            Check($"Transformer {t.Name}", t.From); Check($"Transformer {t.Name}", t.To);
            if (t.Phases.Count() != 3 && t.Connection != TransformerConnection.WyeGWyeG)
                e.Add($"Transformer {t.Name}: partial-phase banks must be wye-wye.");
            if (t.KvaRating <= 0 || t.KvPrimary <= 0 || t.KvSecondary <= 0) e.Add($"Transformer {t.Name}: ratings must be positive.");
        }
        foreach (var r in Regulators) { Check($"Regulator {r.Name}", r.From); Check($"Regulator {r.Name}", r.To); }
        foreach (var l in Loads) Check($"Load {l.Name}", l.Bus);
        foreach (var c in Capacitors) Check($"Capacitor {c.Name}", c.Bus);
        foreach (var b in Buses) if (b.BaseKvLL <= 0) e.Add($"Bus {b.Name}: base kV must be positive.");
        return e;
    }

    public override string ToString() =>
        $"{Name}: {Buses.Count} buses, {Lines.Count} lines, {Transformers.Count} transformers, {Regulators.Count} regulators, {Loads.Count} loads, {Capacitors.Count} capacitors";
}
