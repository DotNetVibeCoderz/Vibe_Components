using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Circuit;

/// <summary>Solver settings for circuit analyses.</summary>
public sealed record CircuitOptions
{
    /// <summary>Conductance added from every node to ground and across junctions [S] (keeps capacitor-only nodes solvable).</summary>
    public double Gmin { get; init; } = 1e-12;
    public int MaxNewtonIterations { get; init; } = 150;
    /// <summary>Absolute tolerance on node voltages / branch currents.</summary>
    public double AbsTol { get; init; } = 1e-9;
    public double RelTol { get; init; } = 1e-6;
    public static CircuitOptions Default { get; } = new();
}

/// <summary>
/// A circuit (netlist) solved by modified nodal analysis (MNA): DC operating point, AC phasor sweep and fixed-step transient
/// with trapezoidal / backward-Euler companion models. Units: V, A, Ω, F, H, s, Hz. Ground is node "0" (alias "gnd").
/// </summary>
public sealed class ElectricCircuit
{
    private readonly List<CircuitElement> _elements = [];
    private readonly Dictionary<string, CircuitElement> _byName = new(StringComparer.OrdinalIgnoreCase);

    public string Title { get; set; } = "circuit";
    public IReadOnlyList<CircuitElement> Elements => _elements;

    public static bool IsGround(string node) => node == "0" || node.Equals("gnd", StringComparison.OrdinalIgnoreCase);

    public T Add<T>(T element) where T : CircuitElement
    {
        if (!_byName.TryAdd(element.Name, element)) throw new ArgumentException($"Duplicate element name '{element.Name}'.");
        _elements.Add(element);
        return element;
    }

    public CircuitElement this[string name] => _byName.TryGetValue(name, out var e) ? e : throw new KeyNotFoundException($"Element '{name}' not found.");

    public ElectricCircuit AddResistor(string name, string a, string b, double ohms) { Add(new Resistor(name, a, b, ohms)); return this; }
    public ElectricCircuit AddCapacitor(string name, string a, string b, double farads, double? initialVoltage = null) { Add(new Capacitor(name, a, b, farads, initialVoltage)); return this; }
    public ElectricCircuit AddInductor(string name, string a, string b, double henries, double? initialCurrent = null) { Add(new Inductor(name, a, b, henries, initialCurrent)); return this; }
    public ElectricCircuit AddVoltageSource(string name, string plus, string minus, Waveform waveform, double acMagnitude = 0, double acPhaseDeg = 0) { Add(new VoltageSource(name, plus, minus, waveform, acMagnitude, acPhaseDeg)); return this; }
    public ElectricCircuit AddCurrentSource(string name, string plus, string minus, Waveform waveform, double acMagnitude = 0, double acPhaseDeg = 0) { Add(new CurrentSource(name, plus, minus, waveform, acMagnitude, acPhaseDeg)); return this; }
    public ElectricCircuit AddVcvs(string name, string plus, string minus, string cPlus, string cMinus, double gain) { Add(new Vcvs(name, plus, minus, cPlus, cMinus, gain)); return this; }
    public ElectricCircuit AddVccs(string name, string plus, string minus, string cPlus, string cMinus, double gm) { Add(new Vccs(name, plus, minus, cPlus, cMinus, gm)); return this; }
    public ElectricCircuit AddSwitch(string name, string a, string b, Func<double, bool> isClosed, double ron = 1e-3, double roff = 1e9) { Add(new Switch(name, a, b, isClosed, ron, roff)); return this; }
    /// <summary>Switch that changes state at <paramref name="toggleTimeS"/> (closed before it when <paramref name="initiallyClosed"/>).</summary>
    public ElectricCircuit AddSwitch(string name, string a, string b, bool initiallyClosed, double toggleTimeS = double.PositiveInfinity, double ron = 1e-3, double roff = 1e9) =>
        AddSwitch(name, a, b, t => t < toggleTimeS ? initiallyClosed : !initiallyClosed, ron, roff);
    public ElectricCircuit AddDiode(string name, string anode, string cathode, double saturationCurrent = 1e-14, double emission = 1.0) { Add(new Diode(name, anode, cathode, saturationCurrent, emission)); return this; }
    public ElectricCircuit AddCoupling(string name, string inductor1, string inductor2, double k) { Add(new MutualInductance(name, inductor1, inductor2, k)); return this; }
    public ElectricCircuit AddIdealTransformer(string name, string p1, string p2, string s1, string s2, double turnsRatio) { Add(new IdealTransformer(name, p1, p2, s1, s2, turnsRatio)); return this; }

    // ---------------------------------------------------------------- topology

    private sealed class Layout
    {
        public required Dictionary<string, int> Nodes;
        public required int Size;
        public required bool Nonlinear;
    }

    private Layout Prepare()
    {
        var nodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Node(string n)
        {
            if (IsGround(n)) return -1;
            if (!nodes.TryGetValue(n, out int i)) { i = nodes.Count; nodes[n] = i; }
            return i;
        }
        foreach (var e in _elements) e.N = e.Nodes.Select(Node).ToArray();
        int size = nodes.Count;
        foreach (var e in _elements)
        {
            if (e.BranchCount > 0) { e.Branch = size; size += e.BranchCount; }
            if (e is MutualInductance k)
            {
                k.L1 = this[k.Inductor1] as Inductor ?? throw new ArgumentException($"{k.Name}: '{k.Inductor1}' is not an inductor.");
                k.L2 = this[k.Inductor2] as Inductor ?? throw new ArgumentException($"{k.Name}: '{k.Inductor2}' is not an inductor.");
            }
        }
        if (nodes.Count == 0) throw new InvalidOperationException("Circuit has no non-ground nodes.");
        return new Layout { Nodes = nodes, Size = size, Nonlinear = _elements.Any(e => e.IsNonlinear) };
    }

    // ---------------------------------------------------------------- real (DC / transient) Newton solve

    private double[] SolveReal(Layout lay, StampContext ctx, double[] start, CircuitOptions opt, out int iterations)
    {
        var x = (double[])start.Clone();
        for (int it = 1; it <= opt.MaxNewtonIterations; it++)
        {
            ctx.Guess = x;
            var s = new Stamper<double>(lay.Size);
            for (int i = 0; i < lay.Nodes.Count; i++) s.A[i, i] += opt.Gmin;
            foreach (var e in _elements) e.StampReal(s, ctx);
            double[] xn;
            try { xn = s.A.Solve(s.B); }
            catch (SingularMatrixException ex) { throw new InvalidOperationException($"Singular circuit matrix (floating node or voltage-source loop?): {ex.Message}", ex); }
            if (!lay.Nonlinear) { iterations = 1; return xn; }
            bool limited = false;
            foreach (var e in _elements) limited |= e.Limit(xn);
            bool converged = !limited;
            for (int i = 0; i < xn.Length && converged; i++)
                if (Math.Abs(xn[i] - x[i]) > opt.AbsTol + opt.RelTol * Math.Max(Math.Abs(xn[i]), Math.Abs(x[i]))) converged = false;
            x = xn;
            if (converged) { iterations = it; return x; }
        }
        throw new InvalidOperationException($"Newton iterations did not converge in {opt.MaxNewtonIterations} iterations (t = {ctx.Time:G4} s).");
    }

    /// <summary>DC operating point: capacitors open, inductors shorted, sources at their t = 0 value.</summary>
    public DcResult OperatingPoint(CircuitOptions? options = null)
    {
        var opt = options ?? CircuitOptions.Default;
        var lay = Prepare();
        var ctx = new StampContext { Mode = AnalysisMode.OperatingPoint, Time = 0, Gmin = opt.Gmin };
        var x = SolveReal(lay, ctx, new double[lay.Size], opt, out int its);
        foreach (var e in _elements) e.Accept(x, ctx);
        var currents = _elements.ToDictionary(e => e.Name, e => e.Current(x, ctx), StringComparer.OrdinalIgnoreCase);
        return new DcResult(lay.Nodes, x, currents, its);
    }

    /// <summary>AC small-signal phasor analysis at the given frequencies [Hz] (linearised about the DC operating point; sources use their AC magnitude/phase).</summary>
    public AcResult AcAnalysis(IEnumerable<double> frequenciesHz, CircuitOptions? options = null)
    {
        var opt = options ?? CircuitOptions.Default;
        if (_elements.Any(e => e.IsNonlinear)) OperatingPoint(opt); // sets diode small-signal conductances
        var lay = Prepare();
        var freqs = frequenciesHz.ToArray();
        var sols = new Complex[freqs.Length][];
        for (int k = 0; k < freqs.Length; k++)
        {
            double w = 2 * Math.PI * freqs[k];
            var s = new Stamper<Complex>(lay.Size);
            for (int i = 0; i < lay.Nodes.Count; i++) s.A[i, i] += opt.Gmin;
            foreach (var e in _elements) e.StampAc(s, w);
            sols[k] = s.A.Solve(s.B);
        }
        var cur = _elements.ToDictionary(e => e.Name, e => sols.Select((x, k) => e.CurrentAc(x, 2 * Math.PI * freqs[k])).ToArray(), StringComparer.OrdinalIgnoreCase);
        return new AcResult(lay.Nodes, freqs, sols, cur);
    }

    /// <summary>Logarithmic AC sweep from <paramref name="startHz"/> to <paramref name="stopHz"/> with <paramref name="pointsPerDecade"/> points per decade.</summary>
    public AcResult AcSweep(double startHz, double stopHz, int pointsPerDecade = 20, CircuitOptions? options = null)
    {
        if (startHz <= 0 || stopHz < startHz) throw new ArgumentOutOfRangeException(nameof(startHz));
        int n = Math.Max(2, (int)Math.Ceiling(Math.Log10(stopHz / startHz) * pointsPerDecade) + 1);
        double a = Math.Log10(startHz), b = Math.Log10(stopHz);
        return AcAnalysis(Enumerable.Range(0, n).Select(i => Math.Pow(10, a + (b - a) * i / (n - 1))), options);
    }

    /// <summary>Fixed-step transient analysis from 0 to <paramref name="stopTimeS"/>.</summary>
    /// <param name="stopTimeS">End time [s].</param>
    /// <param name="stepS">Time step [s].</param>
    /// <param name="method">Integration rule.</param>
    /// <param name="useInitialConditions">true = skip the operating point and start from capacitor/inductor initial conditions (SPICE UIC; other states zero).</param>
    public TransientResult Transient(double stopTimeS, double stepS, IntegrationMethod method = IntegrationMethod.Trapezoidal,
        bool useInitialConditions = false, CircuitOptions? options = null)
    {
        if (stepS <= 0 || stopTimeS <= 0) throw new ArgumentOutOfRangeException(nameof(stepS));
        var opt = options ?? CircuitOptions.Default;
        var lay = Prepare();
        double[] x;
        var opCtx = new StampContext { Mode = AnalysisMode.OperatingPoint, Time = 0, Gmin = opt.Gmin };
        if (useInitialConditions)
        {
            x = new double[lay.Size];
            foreach (var e in _elements.OfType<Capacitor>())
                if (e.InitialVoltage is double v0)
                {
                    if (e.N[0] >= 0 && e.N[1] < 0) x[e.N[0]] = v0;
                    else if (e.N[1] >= 0 && e.N[0] < 0) x[e.N[1]] = -v0;
                }
            foreach (var e in _elements.OfType<Inductor>()) x[e.Branch] = e.InitialCurrent ?? 0;
        }
        else
        {
            x = SolveReal(lay, opCtx, new double[lay.Size], opt, out _);
            foreach (var e in _elements) e.Accept(x, opCtx);
        }
        foreach (var e in _elements) e.InitTransient(x, useInitialConditions);

        int steps = (int)Math.Round(stopTimeS / stepS);
        var time = new double[steps + 1];
        var states = new double[steps + 1][];
        var cur = _elements.ToDictionary(e => e.Name, _ => new double[steps + 1], StringComparer.OrdinalIgnoreCase);
        time[0] = 0; states[0] = x;
        foreach (var e in _elements) cur[e.Name][0] = e is Capacitor or Inductor && useInitialConditions ? (e is Inductor l ? l.InitialCurrent ?? 0 : 0) : e.Current(x, opCtx);
        var ctx = new StampContext { Mode = AnalysisMode.Transient, Dt = stepS, Method = method, Gmin = opt.Gmin };
        for (int k = 1; k <= steps; k++)
        {
            ctx.Time = k * stepS;
            x = SolveReal(lay, ctx, x, opt, out _);
            foreach (var e in _elements) cur[e.Name][k] = e.Current(x, ctx);
            foreach (var e in _elements) e.Accept(x, ctx);
            time[k] = ctx.Time; states[k] = x;
        }
        return new TransientResult(lay.Nodes, time, states, cur);
    }
}
