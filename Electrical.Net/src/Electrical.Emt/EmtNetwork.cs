namespace Electrical.Emt;

/// <summary>
/// Element container of an electromagnetic-transients study. Nodes are referenced by name; "0", "gnd" and "ground" are the reference.
/// All values are SI: V, A, Ω, H, F, s. Three-phase helpers name phase nodes <c>bus.a</c>, <c>bus.b</c>, <c>bus.c</c>.
/// </summary>
public sealed class EmtNetwork
{
    private readonly List<EmtElement> _elements = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public string Title { get; set; } = "emt";

    public IReadOnlyList<EmtElement> Elements => _elements;

    /// <summary>True for the reference node names "0", "gnd", "ground" (case-insensitive).</summary>
    public static bool IsGround(string node) =>
        node == "0" || node.Equals("gnd", StringComparison.OrdinalIgnoreCase) || node.Equals("ground", StringComparison.OrdinalIgnoreCase);

    /// <summary>Phase node names of a three-phase bus: <c>bus.a</c>, <c>bus.b</c>, <c>bus.c</c> (ground stays ground).</summary>
    public static string[] Phases(string bus) => IsGround(bus) ? [bus, bus, bus] : [bus + ".a", bus + ".b", bus + ".c"];

    /// <summary>Adds an element; names must be unique.</summary>
    public T Add<T>(T element) where T : EmtElement
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_names.Add(element.Name)) throw new ArgumentException($"Duplicate element name '{element.Name}'.", nameof(element));
        _elements.Add(element);
        return element;
    }

    // ------------------------------------------------------------------ single-phase elements

    /// <summary>Resistor R (Ω).</summary>
    public EmtNetwork AddResistor(string name, string a, string b, double ohms)
    {
        if (ohms <= 0) throw new ArgumentOutOfRangeException(nameof(ohms), "Resistance must be positive.");
        Add(new RlcBranch(name, a, b, ohms, 0, 0)); return this;
    }

    /// <summary>Inductor L (H) with optional initial current (A, a → b).</summary>
    public EmtNetwork AddInductor(string name, string a, string b, double henries, double initialCurrent = 0)
    {
        if (henries <= 0) throw new ArgumentOutOfRangeException(nameof(henries));
        Add(new RlcBranch(name, a, b, 0, henries, 0, initialCurrent)); return this;
    }

    /// <summary>Capacitor C (F) with optional initial voltage (V, a positive).</summary>
    public EmtNetwork AddCapacitor(string name, string a, string b, double farads, double initialVoltage = 0)
    {
        if (farads <= 0) throw new ArgumentOutOfRangeException(nameof(farads));
        Add(new RlcBranch(name, a, b, 0, 0, farads, 0, initialVoltage)); return this;
    }

    /// <summary>Series R (Ω) – L (H) branch.</summary>
    public EmtNetwork AddSeriesRL(string name, string a, string b, double ohms, double henries)
    {
        Add(new RlcBranch(name, a, b, ohms, henries, 0)); return this;
    }

    /// <summary>Series R (Ω) – L (H) – C (F) branch.</summary>
    public EmtNetwork AddSeriesRLC(string name, string a, string b, double ohms, double henries, double farads)
    {
        Add(new RlcBranch(name, a, b, ohms, henries, farads)); return this;
    }

    /// <summary>Time-controlled switch (times in s; R_on/R_off in Ω).</summary>
    public EmtNetwork AddSwitch(string name, string a, string b, double closeTime = double.PositiveInfinity, double openTime = double.PositiveInfinity,
        bool initiallyClosed = false, bool openAtCurrentZero = false, double ron = 1e-5, double roff = 1e7)
    {
        Add(new EmtSwitch(name, a, b, initiallyClosed, closeTime, openTime, openAtCurrentZero, ron, roff)); return this;
    }

    /// <summary>Ideal diode (two-state switch).</summary>
    public EmtNetwork AddDiode(string name, string anode, string cathode, double ron = 1e-3, double roff = 1e7)
    {
        Add(new EmtDiode(name, anode, cathode, ron, roff)); return this;
    }

    /// <summary>Ideal thyristor: a diode that can only turn on while <paramref name="gate"/>(t) is true.</summary>
    public EmtNetwork AddThyristor(string name, string anode, string cathode, Func<double, bool> gate, double ron = 1e-3, double roff = 1e7)
    {
        ArgumentNullException.ThrowIfNull(gate);
        Add(new EmtDiode(name, anode, cathode, ron, roff, gate)); return this;
    }

    /// <summary>Voltage source node → ground (V) with internal R (Ω) + L (H); ideal when both are zero.</summary>
    public EmtNetwork AddVoltageSource(string name, string node, EmtWaveform waveform, double r = 0, double l = 0)
    {
        if (IsGround(node)) throw new ArgumentException("A voltage source needs a non-ground node.", nameof(node));
        Add(new EmtVoltageSource(name, node, waveform, r, l)); return this;
    }

    /// <summary>Current source (A) pushing current from <paramref name="from"/> into <paramref name="to"/>.</summary>
    public EmtNetwork AddCurrentSource(string name, string from, string to, EmtWaveform waveform)
    {
        Add(new EmtCurrentSource(name, from, to, waveform)); return this;
    }

    /// <summary>Single-phase transformer with ratio V_p/V_s and leakage R (Ω) + L (H) referred to the secondary.</summary>
    public EmtNetwork AddTransformer(string name, string p1, string p2, string s1, string s2, double ratio, double r, double l)
    {
        if (ratio <= 0) throw new ArgumentOutOfRangeException(nameof(ratio));
        if (r <= 0 && l <= 0) throw new ArgumentException("Leakage impedance must be non-zero.");
        Add(new EmtTransformer(name, p1, p2, s1, s2, ratio, r, l)); return this;
    }

    /// <summary>Single-phase Bergeron line from per-km data (Ω/km, H/km, F/km) and length (km).</summary>
    public EmtNetwork AddBergeronLine(string name, string from, string to, double rPerKm, double lPerKm, double cPerKm, double lengthKm)
    {
        Add(BergeronLine.SinglePhase(name, from, to, rPerKm, lPerKm, cPerKm, lengthKm)); return this;
    }

    /// <summary>
    /// Constant-parameter multiphase π section: series R (Ω), L (H) matrices and total shunt C (F) matrix, half at each end.
    /// Adds elements <c>name</c> (series), <c>name.Ck</c> and <c>name.Cm</c> (shunts, omitted when C is null).
    /// </summary>
    public EmtNetwork AddPiSection(string name, string[] from, string[] to, double[,] r, double[,] l, double[,]? c)
    {
        Add(new CoupledRlBranch(name, from, to, r, l));
        if (c is not null)
        {
            var half = Scale(c, 0.5);
            Add(new ShuntCapacitance(name + ".Ck", from, half));
            Add(new ShuntCapacitance(name + ".Cm", to, half));
        }
        return this;
    }

    // ------------------------------------------------------------------ three-phase helpers

    /// <summary>
    /// Balanced three-phase voltage source at <paramref name="bus"/> (nodes bus.a/b/c): line-to-line RMS voltage (V), frequency (Hz),
    /// phase-a angle (degrees, cosine reference), internal R (Ω) + L (H) per phase. Elements are named <c>name.a</c>, <c>name.b</c>, <c>name.c</c>.
    /// </summary>
    public EmtNetwork AddThreePhaseSource(string name, string bus, double lineVoltageRms, double frequencyHz, double phaseDeg = 0,
        double r = 0, double l = 0, double startTime = 0)
    {
        double vpk = lineVoltageRms * Math.Sqrt(2.0 / 3.0);
        var n = Phases(bus);
        string[] ph = ["a", "b", "c"];
        for (int p = 0; p < 3; p++)
            AddVoltageSource($"{name}.{ph[p]}", n[p], EmtWaveform.Cosine(vpk, frequencyHz, phaseDeg - 120 * p, startTime), r, l);
        return this;
    }

    /// <summary>Wye-connected R (Ω) – L (H) – C (F) load per phase from bus to <paramref name="neutral"/> (default solidly grounded).</summary>
    public EmtNetwork AddThreePhaseLoad(string name, string bus, double r, double l = 0, double c = 0, string neutral = "0")
    {
        var n = Phases(bus);
        string[] ph = ["a", "b", "c"];
        for (int p = 0; p < 3; p++) Add(new RlcBranch($"{name}.{ph[p]}", n[p], neutral, r, l, c));
        return this;
    }

    /// <summary>Three-phase series R-L-C element between two buses (per phase, uncoupled).</summary>
    public EmtNetwork AddThreePhaseSeries(string name, string fromBus, string toBus, double r, double l = 0, double c = 0)
    {
        var a = Phases(fromBus); var b = Phases(toBus);
        string[] ph = ["a", "b", "c"];
        for (int p = 0; p < 3; p++) Add(new RlcBranch($"{name}.{ph[p]}", a[p], b[p], r, l, c));
        return this;
    }

    /// <summary>Wye-grounded three-phase capacitor bank C (F per phase) at a bus.</summary>
    public EmtNetwork AddThreePhaseCapacitorBank(string name, string bus, double farads, string neutral = "0") =>
        AddThreePhaseLoad(name, bus, 0, 0, farads, neutral);

    /// <summary>Three-pole time-controlled breaker between two buses; per-pole close/open times (s) may be given to model pole scatter.</summary>
    public EmtNetwork AddThreePhaseSwitch(string name, string fromBus, string toBus, double closeTime = double.PositiveInfinity,
        double openTime = double.PositiveInfinity, bool initiallyClosed = false, bool openAtCurrentZero = false,
        double[]? poleCloseTimes = null, double[]? poleOpenTimes = null)
    {
        var a = Phases(fromBus); var b = Phases(toBus);
        string[] ph = ["a", "b", "c"];
        for (int p = 0; p < 3; p++)
            AddSwitch($"{name}.{ph[p]}", a[p], b[p], poleCloseTimes?[p] ?? closeTime, poleOpenTimes?[p] ?? openTime, initiallyClosed, openAtCurrentZero);
        return this;
    }

    /// <summary>Transposed three-phase Bergeron line between two buses from sequence data per km (Ω/km, H/km, F/km) and length (km).</summary>
    public EmtNetwork AddThreePhaseLine(string name, string fromBus, string toBus, double r1PerKm, double l1PerKm, double c1PerKm,
        double r0PerKm, double l0PerKm, double c0PerKm, double lengthKm)
    {
        Add(BergeronLine.ThreePhaseTransposed(name, Phases(fromBus), Phases(toBus), r1PerKm, l1PerKm, c1PerKm, r0PerKm, l0PerKm, c0PerKm, lengthKm));
        return this;
    }

    /// <summary>
    /// Transposed three-phase π section between two buses from sequence data per km and length (km): self = (Z0 + 2Z1)/3, mutual = (Z0 − Z1)/3,
    /// likewise for the capacitance matrix.
    /// </summary>
    public EmtNetwork AddThreePhasePiSection(string name, string fromBus, string toBus, double r1PerKm, double l1PerKm, double c1PerKm,
        double r0PerKm, double l0PerKm, double c0PerKm, double lengthKm)
    {
        var r = SequenceToPhase(r1PerKm * lengthKm, r0PerKm * lengthKm);
        var l = SequenceToPhase(l1PerKm * lengthKm, l0PerKm * lengthKm);
        var c = SequenceToPhase(c1PerKm * lengthKm, c0PerKm * lengthKm);
        return AddPiSection(name, Phases(fromBus), Phases(toBus), r, l, c);
    }

    /// <summary>
    /// Three-phase transformer bank of single-phase units. <paramref name="connection"/>: "YNyn" (both wye grounded) or "YNd" (wye-grounded
    /// primary, delta secondary a–b, b–c, c–a). Ratio = per-unit winding voltage ratio, leakage R (Ω) + L (H) referred to the secondary winding.
    /// </summary>
    public EmtNetwork AddThreePhaseTransformer(string name, string primaryBus, string secondaryBus, double windingRatio, double r, double l,
        string connection = "YNyn")
    {
        var p = Phases(primaryBus); var s = Phases(secondaryBus);
        string[] ph = ["a", "b", "c"];
        bool delta = connection.Equals("YNd", StringComparison.OrdinalIgnoreCase);
        if (!delta && !connection.Equals("YNyn", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Supported connections: YNyn, YNd.", nameof(connection));
        for (int k = 0; k < 3; k++)
        {
            if (delta) AddTransformer($"{name}.{ph[k]}", p[k], "0", s[k], s[(k + 1) % 3], windingRatio, r, l);
            else AddTransformer($"{name}.{ph[k]}", p[k], "0", s[k], "0", windingRatio, r, l);
        }
        return this;
    }

    /// <summary>3×3 phase matrix of a transposed element: diagonal (x0 + 2x1)/3, off-diagonal (x0 − x1)/3.</summary>
    public static double[,] SequenceToPhase(double x1, double x0)
    {
        double s = (x0 + 2 * x1) / 3, m = (x0 - x1) / 3;
        return new double[,] { { s, m, m }, { m, s, m }, { m, m, s } };
    }

    private static double[,] Scale(double[,] a, double k)
    {
        var r = (double[,])a.Clone();
        for (int i = 0; i < r.GetLength(0); i++)
            for (int j = 0; j < r.GetLength(1); j++) r[i, j] *= k;
        return r;
    }
}
