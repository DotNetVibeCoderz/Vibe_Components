using System.Diagnostics;
using Electrical.Core.Numerics;
using Electrical.Native;

namespace Electrical.Emt;

/// <summary>
/// Electromagnetic-transients solver using Dommel's nodal method with a fixed time step: every element is replaced by its
/// trapezoidal companion model (conductance + history current source), ideal voltage sources are handled by partitioning the
/// node set into unknown (A) and known (B) nodes, and the real system <c>G_AA·v_A = i_A − G_AB·v_B</c> is solved each step.
/// <c>G</c> is constant between topology changes: it is factorized once with <see cref="SparseLU{T}"/> and refactorized
/// (reusing the ordering) only when a switch, diode or thyristor changes state. The branch-history recurrence and the
/// forward/backward substitution run in <see cref="EmtKernels"/> (Rust when available).
/// </summary>
public sealed class EmtSimulation
{
    private enum Method { Trapezoidal, BackwardEuler }

    private readonly EmtOptions _o;
    private readonly double _dt;
    private readonly string[] _nodeNames;
    private readonly bool[] _internalNode;
    private readonly Dictionary<string, int> _nodeIndex = new(StringComparer.Ordinal);
    private int _nA, _nv;

    // constant conductance triplets (full nv×nv)
    private readonly List<(int R, int C, double V)> _constant = [];

    // RLC kernel arrays
    private int[] _bFrom = [], _bTo = [];
    private double[] _coefTr = [], _coefBe = [], _state = [];
    private readonly List<(string Name, int Slot)> _rlcSlots = [];

    private readonly List<ResistorC> _resistors = [];
    private readonly List<SwitchC> _switches = [];
    private readonly List<KnownSource> _known = [];
    private readonly List<CurrentSourceC> _isources = [];
    private readonly List<CoupledC> _coupled = [];
    private readonly List<TransformerC> _transformers = [];
    private readonly List<LineC> _lines = [];

    // vectors
    private double[] _v = [], _hist = [], _bFull = [], _rhs = [], _x = [], _work = [];

    // matrix
    private int[] _gPtr = [], _gIdx = [];
    private double[] _gVal = [];
    private SparseLU<double>? _lu;
    private SparseLUFactors<double>? _factors;
    private int[]? _ordering;
    private int _factorizations;
    private bool _ran;

    // recording
    private readonly List<string> _currentNames = [];
    private double[] _currentNow = [];

    /// <summary>Compiles <paramref name="network"/> for the given options (validates element data and the time step).</summary>
    public EmtSimulation(EmtNetwork network, EmtOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        _o = options ?? EmtOptions.Default;
        if (!(_o.TimeStep > 0)) throw new ArgumentOutOfRangeException(nameof(options), "TimeStep must be positive.");
        if (!(_o.StopTime > 0)) throw new ArgumentOutOfRangeException(nameof(options), "StopTime must be positive.");
        if (_o.RecordEvery < 1) throw new ArgumentOutOfRangeException(nameof(options), "RecordEvery must be ≥ 1.");
        _dt = _o.TimeStep;
        Network = network;
        var (names, internalFlags) = Compile(network);
        _nodeNames = names; _internalNode = internalFlags;
    }

    public EmtNetwork Network { get; }

    /// <summary>Number of non-ground nodes (including internal source nodes).</summary>
    public int NodeCount => _nv;

    /// <summary>Number of unknown-voltage nodes (size of the factorized matrix).</summary>
    public int UnknownCount => _nA;

    /// <summary>Simulates a network and returns the recorded channels.</summary>
    public static EmtResult Simulate(EmtNetwork network, EmtOptions? options = null) => new EmtSimulation(network, options).Run();

    // ===================================================================================== compilation

    private int Node(string name) => EmtNetwork.IsGround(name) ? -1 : _nodeIndex[name];

    private (string[], bool[]) Compile(EmtNetwork net)
    {
        // 1. nodes: known (ideal-source) nodes go last
        var unknown = new List<string>(); var known = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var internalSet = new HashSet<string>(StringComparer.Ordinal);
        var internalList = new List<string>();
        var knownSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in net.Elements)
            if (e is EmtVoltageSource vs)
            {
                string n = vs.R == 0 && vs.L == 0 ? vs.Node : vs.Name + "#src";
                if (n != vs.Node && internalSet.Add(n)) internalList.Add(n);
                if (!knownSet.Add(n)) throw new InvalidOperationException($"Node '{n}' is driven by more than one ideal voltage source.");
            }
        void Touch(string n)
        {
            if (EmtNetwork.IsGround(n) || !seen.Add(n)) return;
            (knownSet.Contains(n) ? known : unknown).Add(n);
        }
        foreach (var e in net.Elements)
            foreach (var n in Terminals(e)) Touch(n);
        foreach (var n in internalList) Touch(n);
        _nA = unknown.Count; _nv = unknown.Count + known.Count;
        var names = unknown.Concat(known).ToArray();
        for (int i = 0; i < names.Length; i++) _nodeIndex[names[i]] = i;
        var internalFlags = names.Select(internalSet.Contains).ToArray();

        // 2. elements
        var rlc = new List<(int F, int T, double[] Tr, double[] Be, double I0, double Vc0, double R)>();
        foreach (var e in net.Elements)
        {
            switch (e)
            {
                case RlcBranch b:
                    AddRlc(b.Name, Node(b.From), Node(b.To), b.R, b.L, b.C, b.InitialCurrent, b.InitialCapacitorVoltage, rlc);
                    break;
                case EmtVoltageSource vs:
                    if (vs.R == 0 && vs.L == 0)
                        _known.Add(new KnownSource(vs.Name, Node(vs.Node), vs.Waveform, Slot(vs.Name)));
                    else
                    {
                        int inner = Node(vs.Name + "#src");
                        _known.Add(new KnownSource(vs.Name + "#src", inner, vs.Waveform, -1));
                        AddRlc(vs.Name, inner, Node(vs.Node), vs.R, vs.L, 0, 0, 0, rlc);
                    }
                    break;
                case EmtCurrentSource cs:
                    _isources.Add(new CurrentSourceC(Node(cs.From), Node(cs.To), cs.Waveform, Slot(cs.Name)));
                    break;
                case EmtSwitch sw:
                    if (sw.Ron <= 0 || sw.Roff <= sw.Ron) throw new ArgumentException($"Switch '{sw.Name}': need 0 < Ron < Roff.");
                    _switches.Add(new SwitchC(Node(sw.A), Node(sw.B), 1 / sw.Ron, 1 / sw.Roff, Slot(sw.Name)) { Switch = sw, Closed = sw.InitiallyClosed });
                    break;
                case EmtDiode d:
                    if (d.Ron <= 0 || d.Roff <= d.Ron) throw new ArgumentException($"Diode '{d.Name}': need 0 < Ron < Roff.");
                    _switches.Add(new SwitchC(Node(d.Anode), Node(d.Cathode), 1 / d.Ron, 1 / d.Roff, Slot(d.Name)) { Diode = d });
                    break;
                case CoupledRlBranch c:
                    _coupled.Add(CoupledC.RL(c, c.From.Select(Node).ToArray(), c.To.Select(Node).ToArray(), _dt, i => Slot($"{c.Name}[{i}]")));
                    break;
                case ShuntCapacitance sc:
                    _coupled.Add(CoupledC.Shunt(sc, sc.Nodes.Select(Node).ToArray(), _dt, i => Slot($"{sc.Name}[{i}]")));
                    break;
                case EmtTransformer tr:
                    _transformers.Add(new TransformerC(tr, [Node(tr.P1), Node(tr.P2), Node(tr.S1), Node(tr.S2)], _dt, Slot(tr.Name), Slot(tr.Name + ".p")));
                    break;
                case BergeronLine bl:
                    _lines.Add(new LineC(bl, bl.From.Select(Node).ToArray(), bl.To.Select(Node).ToArray(), _dt,
                        i => Slot($"{bl.Name}[{i}]"), i => Slot($"{bl.Name}.m[{i}]")));
                    break;
                default:
                    throw new NotSupportedException($"Element type {e.GetType().Name} is not supported by the EMT solver.");
            }
        }

        int nb = rlc.Count;
        _bFrom = new int[nb]; _bTo = new int[nb];
        _coefTr = new double[6 * nb]; _coefBe = new double[6 * nb]; _state = new double[3 * nb];
        for (int k = 0; k < nb; k++)
        {
            var r = rlc[k];
            _bFrom[k] = r.F; _bTo[k] = r.T;
            r.Tr.CopyTo(_coefTr, 6 * k); r.Be.CopyTo(_coefBe, 6 * k);
            _state[3 * k] = r.I0; _state[3 * k + 1] = r.Vc0;
            // initial history from the initial state: vb(0) = R·i0 + vc0
            double vb0 = r.R * r.I0 + r.Vc0;
            _state[3 * k + 2] = r.Tr[0] * (r.Tr[3] * vb0 + r.Tr[4] * r.I0 + r.Tr[5] * r.Vc0);
            StampScalar(r.F, r.T, r.Tr[0]);
        }
        foreach (var res in _resistors) StampScalar(res.F, res.T, res.G);
        foreach (var c in _coupled) StampMatrix(c.F, c.T, c.G, c.N);
        foreach (var t in _transformers) t.Stamp(_constant);
        foreach (var l in _lines) { StampMatrix(l.K, null, l.Gph, l.N); StampMatrix(l.M, null, l.Gph, l.N); }

        _currentNow = new double[_currentNames.Count];
        return (names, internalFlags);

        int Slot(string name)
        {
            if (!_o.RecordCurrents) return -1;
            _currentNames.Add(name);
            return _currentNames.Count - 1;
        }

        void AddRlc(string name, int f, int t, double r, double l, double c, double i0, double vc0,
            List<(int, int, double[], double[], double, double, double)> list)
        {
            if (r < 0 || l < 0 || c < 0) throw new ArgumentException($"Branch '{name}': R, L, C must be non-negative.");
            if (l == 0 && c == 0)
            {
                if (r <= 0) throw new ArgumentException($"Branch '{name}': a resistor needs R > 0.");
                _resistors.Add(new ResistorC(f, t, 1 / r, Slot(name)));
                return;
            }
            double beta = c > 0 ? _dt / (2 * c) : 0;
            double twoLdt = 2 * l / _dt;
            double g = 1 / (r + twoLdt + beta);
            double[] tr = [g, beta, beta, 1, -(r - twoLdt + beta), -2];
            double[] be = [g, beta, 0, 0, twoLdt, -1];
            _rlcSlots.Add((name, Slot(name)));
            list.Add((f, t, tr, be, c > 0 || l > 0 ? i0 : 0, c > 0 ? vc0 : 0, r));
        }
    }

    private static IEnumerable<string> Terminals(EmtElement e) => e switch
    {
        RlcBranch b => [b.From, b.To],
        EmtVoltageSource v => [v.Node],
        EmtCurrentSource c => [c.From, c.To],
        EmtSwitch s => [s.A, s.B],
        EmtDiode d => [d.Anode, d.Cathode],
        CoupledRlBranch c => c.From.Concat(c.To),
        ShuntCapacitance s => s.Nodes,
        EmtTransformer t => [t.P1, t.P2, t.S1, t.S2],
        BergeronLine l => l.From.Concat(l.To),
        _ => [],
    };

    private void StampScalar(int f, int t, double g)
    {
        if (f >= 0) _constant.Add((f, f, g));
        if (t >= 0) _constant.Add((t, t, g));
        if (f >= 0 && t >= 0) { _constant.Add((f, t, -g)); _constant.Add((t, f, -g)); }
    }

    private void StampMatrix(int[] f, int[]? t, double[] g, int n)
    {
        for (int p = 0; p < n; p++)
            for (int q = 0; q < n; q++)
            {
                double y = g[p * n + q];
                if (y == 0) continue;
                int fp = f[p], fq = f[q];
                int tp = t is null ? -1 : t[p], tq = t is null ? -1 : t[q];
                if (fp >= 0 && fq >= 0) _constant.Add((fp, fq, y));
                if (tp >= 0 && tq >= 0) _constant.Add((tp, tq, y));
                if (fp >= 0 && tq >= 0) _constant.Add((fp, tq, -y));
                if (tp >= 0 && fq >= 0) _constant.Add((tp, fq, -y));
            }
    }

    // ===================================================================================== matrix

    private void Factorize()
    {
        var full = new SparseBuilder<double>(_nv, _nv);
        foreach (var (r, c, v) in _constant) full.Add(r, c, v);
        foreach (var s in _switches)
        {
            double g = s.Closed ? s.Gon : s.Goff;
            if (s.F >= 0) full.Add(s.F, s.F, g);
            if (s.T >= 0) full.Add(s.T, s.T, g);
            if (s.F >= 0 && s.T >= 0) { full.Add(s.F, s.T, -g); full.Add(s.T, s.F, -g); }
        }
        var gm = full.ToCsr();
        _gPtr = new int[_nv + 1];
        for (int i = 0; i < _nv; i++) _gPtr[i + 1] = _gPtr[i] + gm.RowIndices(i).Length;
        _gIdx = new int[_gPtr[_nv]]; _gVal = new double[_gPtr[_nv]];
        for (int i = 0; i < _nv; i++) { gm.RowIndices(i).CopyTo(_gIdx.AsSpan(_gPtr[i])); gm.RowValues(i).CopyTo(_gVal.AsSpan(_gPtr[i])); }
        _factorizations++;
        if (_nA == 0) return;
        var a = new SparseBuilder<double>(_nA, _nA);
        for (int i = 0; i < _nA; i++)
            for (int p = _gPtr[i]; p < _gPtr[i + 1]; p++)
                if (_gIdx[p] < _nA) a.Add(i, _gIdx[p], _gVal[p]);
        var am = a.ToCsr();
        try
        {
            _lu = SparseLU<double>.Factorize(am, _ordering);
        }
        catch (Exception ex) when (ex is SingularMatrixException or InvalidOperationException)
        {
            throw new InvalidOperationException("EMT network matrix is singular: check for floating nodes (every node needs a conductive or capacitive path).", ex);
        }
        _ordering ??= _lu.Ordering;
        _factors = _lu.ExportFactors();
    }

    // ===================================================================================== stepping

    /// <summary>Runs the simulation from t = 0 (de-energized network unless initial conditions are given) to <see cref="EmtOptions.StopTime"/>.</summary>
    public EmtResult Run()
    {
        if (_ran) throw new InvalidOperationException("An EmtSimulation can be run once; create a new instance for another run.");
        _ran = true;
        var sw = Stopwatch.StartNew();
        bool native = _o.PreferNative && EmtKernels.IsNativeAvailable;
        int steps = (int)Math.Round(_o.StopTime / _dt);
        int nRec = steps / _o.RecordEvery + 1;
        var time = new double[nRec];
        var vRec = new double[_nv][];
        for (int i = 0; i < _nv; i++) vRec[i] = new double[nRec];
        var iRec = new double[_currentNames.Count][];
        for (int i = 0; i < iRec.Length; i++) iRec[i] = new double[nRec];

        _v = new double[_nv]; _hist = new double[_nv]; _bFull = new double[_nv];
        _rhs = new double[_nA]; _x = new double[_nA]; _work = new double[_nA];
        // initial history injections of RLC branches with initial conditions
        for (int k = 0; k < _bFrom.Length; k++)
        {
            double ih = _state[3 * k + 2];
            if (_bFrom[k] >= 0) _hist[_bFrom[k]] -= ih;
            if (_bTo[k] >= 0) _hist[_bTo[k]] += ih;
        }
        foreach (var l in _lines) l.Reset();
        foreach (var k in _known) _v[k.Node] = k.Waveform.At(0);
        int rec = 0;
        Record(0, 0);

        Factorize();
        var next = Method.Trapezoidal;
        int totalSteps = 0;
        bool cdaPending = false;
        for (int n = 1; n <= steps; n++)
        {
            double t = n * _dt;
            if (cdaPending)
            {
                // first backward-Euler half step t(n-1) → t(n-1) + Δt/2 (not recorded)
                SolveStep(t - 0.5 * _dt, n - 0.5, native, out _);
                Update(Method.BackwardEuler, Method.BackwardEuler, native, fullStep: false);
                totalSteps++;
                next = Method.BackwardEuler;
                cdaPending = false;
            }
            bool changed = UpdateSwitchSchedule(t);
            if (changed) Factorize();
            SolveStep(t, n, native, out bool diodeChanged);
            changed |= diodeChanged;
            var cur = next;
            next = changed && _o.CriticalDampingAdjustment ? Method.BackwardEuler : Method.Trapezoidal;
            cdaPending = changed && _o.CriticalDampingAdjustment;
            Update(cur, next, native, fullStep: true);
            totalSteps++;
            if (n % _o.RecordEvery == 0) Record(++rec, t);
        }
        sw.Stop();

        var vd = new Dictionary<string, EmtChannel>(StringComparer.Ordinal);
        double recDt = _dt * _o.RecordEvery;
        for (int i = 0; i < _nv; i++)
            if (!_internalNode[i]) vd[_nodeNames[i]] = new EmtChannel(_nodeNames[i], "V", 0, recDt, vRec[i]);
        var id = new Dictionary<string, EmtChannel>(StringComparer.Ordinal);
        for (int i = 0; i < _currentNames.Count; i++) id[_currentNames[i]] = new EmtChannel(_currentNames[i], "A", 0, recDt, iRec[i]);
        // single-phase aliases for multiphase elements
        foreach (var name in _currentNames)
        {
            if (!name.EndsWith("[0]", StringComparison.Ordinal)) continue;
            string baseName = name[..^3];
            if (!id.ContainsKey(baseName + "[1]") && !id.ContainsKey(baseName)) id[baseName] = id[name];
        }
        return new EmtResult(time, vd, id, totalSteps, _factorizations, native ? EmtKernels.Backend : "managed", sw.Elapsed);

        void Record(int r, double tt)
        {
            time[r] = tt;
            for (int i = 0; i < _nv; i++) vRec[i][r] = _v[i];
            for (int i = 0; i < iRec.Length; i++) iRec[i][r] = _currentNow[i];
        }
    }

    /// <summary>Applies the switch schedule at time t (s); returns true when the topology changed.</summary>
    private bool UpdateSwitchSchedule(double t)
    {
        bool changed = false;
        double eps = 1e-9 * _dt;
        foreach (var s in _switches)
        {
            if (s.Switch is not { } sw) continue;
            if (!s.CloseDone && t >= sw.CloseTime - eps)
            {
                s.CloseDone = true;
                if (!s.Closed) { s.Closed = true; changed = true; }
            }
            if (!s.OpenDone && t >= sw.OpenTime - eps && s.Closed)
            {
                if (!sw.OpenAtCurrentZero) { s.OpenDone = true; s.Closed = false; changed = true; }
                else
                {
                    // open when the current, linearly extrapolated from the last two steps, crosses zero within this step
                    double predicted = 2 * s.LastCurrent - s.PrevCurrent;
                    if (s.LastCurrent == 0 || Math.Sign(predicted) != Math.Sign(s.LastCurrent))
                    { s.OpenDone = true; s.Closed = false; changed = true; }
                }
            }
        }
        return changed;
    }

    private void SolveStep(double t, double pos, bool native, out bool diodeChanged)
    {
        diodeChanged = false;
        for (int iter = 0; ; iter++)
        {
            BuildRhs(t, pos);
            Solve(native);
            if (!UpdateDiodes(t) || iter >= _o.MaxSwitchIterations) break;
            diodeChanged = true;
            Factorize();
        }
    }

    private void BuildRhs(double t, double pos)
    {
        Array.Copy(_hist, _bFull, _nv);
        foreach (var k in _known) _v[k.Node] = k.Waveform.At(t);
        foreach (var c in _isources)
        {
            double i = c.Waveform.At(t);
            c.Value = i;
            if (c.F >= 0) _bFull[c.F] -= i;
            if (c.T >= 0) _bFull[c.T] += i;
        }
        foreach (var l in _lines) l.History(pos, _bFull);
        for (int i = 0; i < _nA; i++)
        {
            double s = _bFull[i];
            for (int p = _gPtr[i]; p < _gPtr[i + 1]; p++)
            {
                int j = _gIdx[p];
                if (j >= _nA) s -= _gVal[p] * _v[j];
            }
            _rhs[i] = s;
        }
    }

    private void Solve(bool native)
    {
        if (_nA == 0) return;
        if (_factors is { } f)
            EmtKernels.LuSolve(f.Permutation, f.LRowPtr, f.LColIdx, f.LValues, f.URowPtr, f.UColIdx, f.UValues, f.Diagonal, _rhs, _x, _work, native);
        else
            _lu!.Solve(_rhs).CopyTo(_x, 0);
        Array.Copy(_x, _v, _nA);
    }

    private bool UpdateDiodes(double t)
    {
        bool changed = false;
        foreach (var s in _switches)
        {
            if (s.Diode is not { } d) continue;
            double vak = Vb(s.F, s.T);
            if (s.Closed)
            {
                if (s.Gon * vak < 0) { s.Closed = false; changed = true; }
            }
            else if (vak > 0 && (d.Gate is null || d.Gate(t))) { s.Closed = true; changed = true; }
        }
        return changed;
    }

    private double Vb(int f, int t) => (f >= 0 ? _v[f] : 0.0) - (t >= 0 ? _v[t] : 0.0);

    private void Update(Method cur, Method next, bool native, bool fullStep)
    {
        // currents of ideal sources (before the history vector is overwritten): i = (G·v)_b − b_b
        foreach (var k in _known)
        {
            if (k.Slot < 0) continue;
            int b = k.Node;
            double s = 0;
            for (int p = _gPtr[b]; p < _gPtr[b + 1]; p++) s += _gVal[p] * _v[_gIdx[p]];
            _currentNow[k.Slot] = s - _bFull[b];
        }
        Array.Clear(_hist);
        EmtKernels.BranchUpdate(_bFrom, _bTo, _v,
            cur == Method.Trapezoidal ? _coefTr : _coefBe, next == Method.Trapezoidal ? _coefTr : _coefBe, _state, _hist, native);
        bool nextBe = next == Method.BackwardEuler;
        foreach (var c in _coupled) c.Update(_v, nextBe, _hist, _currentNow);
        foreach (var t in _transformers) t.Update(_v, nextBe, _hist, _currentNow);
        foreach (var l in _lines) l.Terminal(_v, fullStep, _currentNow);
        if (!fullStep) return;
        for (int k = 0; k < _rlcSlots.Count; k++)
            if (_rlcSlots[k].Slot >= 0) _currentNow[_rlcSlots[k].Slot] = _state[3 * k];
        foreach (var r in _resistors) if (r.Slot >= 0) _currentNow[r.Slot] = r.G * Vb(r.F, r.T);
        foreach (var s in _switches)
        {
            double i = (s.Closed ? s.Gon : s.Goff) * Vb(s.F, s.T);
            s.PrevCurrent = s.LastCurrent; s.LastCurrent = i;
            if (s.Slot >= 0) _currentNow[s.Slot] = i;
        }
        foreach (var c in _isources) if (c.Slot >= 0) _currentNow[c.Slot] = c.Value;
    }

    // ===================================================================================== compiled components

    private sealed record ResistorC(int F, int T, double G, int Slot);

    private sealed record KnownSource(string Name, int Node, EmtWaveform Waveform, int Slot);

    private sealed class CurrentSourceC(int f, int t, EmtWaveform w, int slot)
    {
        public int F = f, T = t, Slot = slot;
        public EmtWaveform Waveform = w;
        public double Value;
    }

    private sealed class SwitchC(int f, int t, double gon, double goff, int slot)
    {
        public readonly int F = f, T = t, Slot = slot;
        public readonly double Gon = gon, Goff = goff;
        public EmtSwitch? Switch;
        public EmtDiode? Diode;
        public bool Closed, CloseDone, OpenDone;
        public double LastCurrent, PrevCurrent;
    }

    /// <summary>Multiphase coupled RL branch or shunt capacitance: i = G·vb + ih, ih' = Pv·vb + Pi·i.</summary>
    private sealed class CoupledC
    {
        public int N;
        public int[] F = [], T = [];
        public double[] G = [], PvTr = [], PiTr = [], PvBe = [], PiBe = [];
        public double[] I = [], Ih = [], Vb = [];
        public int[] Slots = [];

        public static CoupledC RL(CoupledRlBranch b, int[] f, int[] t, double dt, Func<int, int> slot)
        {
            int n = f.Length;
            Check(b.Name, n, t.Length, b.R, b.L);
            var m = new double[n * n];
            for (int p = 0; p < n; p++) for (int q = 0; q < n; q++) m[p * n + q] = b.R[p, q] + 2 * b.L[p, q] / dt;
            var g = Dense.Inverse(m, n, b.Name);
            var a = new double[n * n]; var a2 = new double[n * n];
            for (int p = 0; p < n; p++) for (int q = 0; q < n; q++) { a[p * n + q] = 2 * b.L[p, q] / dt - b.R[p, q]; a2[p * n + q] = 2 * b.L[p, q] / dt; }
            return Make(n, f, t, g, g, Dense.Mul(g, a, n), new double[n * n], Dense.Mul(g, a2, n), slot);
        }

        public static CoupledC Shunt(ShuntCapacitance s, int[] nodes, double dt, Func<int, int> slot)
        {
            int n = nodes.Length;
            Check(s.Name, n, n, s.C, s.C);
            var g = new double[n * n]; var neg = new double[n * n]; var negI = new double[n * n];
            for (int p = 0; p < n; p++) for (int q = 0; q < n; q++) { g[p * n + q] = 2 * s.C[p, q] / dt; neg[p * n + q] = -g[p * n + q]; }
            for (int p = 0; p < n; p++) negI[p * n + p] = -1;
            return Make(n, nodes, Enumerable.Repeat(-1, n).ToArray(), g, neg, negI, neg, new double[n * n], slot);
        }

        private static void Check(string name, int n, int nt, double[,] a, double[,] b)
        {
            if (n == 0 || nt != n || a.GetLength(0) != n || a.GetLength(1) != n || b.GetLength(0) != n || b.GetLength(1) != n)
                throw new ArgumentException($"Element '{name}': terminal count and matrix dimensions must agree.");
        }

        private static CoupledC Make(int n, int[] f, int[] t, double[] g, double[] pvTr, double[] piTr, double[] pvBe, double[] piBe, Func<int, int> slot) => new()
        {
            N = n, F = f, T = t, G = g, PvTr = pvTr, PiTr = piTr, PvBe = pvBe, PiBe = piBe,
            I = new double[n], Ih = new double[n], Vb = new double[n], Slots = Enumerable.Range(0, n).Select(slot).ToArray(),
        };

        public void Update(double[] v, bool nextBe, double[] hist, double[] cur)
        {
            for (int p = 0; p < N; p++) Vb[p] = (F[p] >= 0 ? v[F[p]] : 0.0) - (T[p] >= 0 ? v[T[p]] : 0.0);
            for (int p = 0; p < N; p++)
            {
                double s = Ih[p];
                for (int q = 0; q < N; q++) s += G[p * N + q] * Vb[q];
                I[p] = s;
            }
            var pv = nextBe ? PvBe : PvTr; var pi = nextBe ? PiBe : PiTr;
            for (int p = 0; p < N; p++)
            {
                double s = 0;
                for (int q = 0; q < N; q++) s += pv[p * N + q] * Vb[q] + pi[p * N + q] * I[q];
                Ih[p] = s;
                if (F[p] >= 0) hist[F[p]] -= s;
                if (T[p] >= 0) hist[T[p]] += s;
                if (Slots[p] >= 0) cur[Slots[p]] = I[p];
            }
        }
    }

    /// <summary>Two-winding transformer: ideal ratio n in series with leakage R-L on the secondary side (incidence a = [1/n, −1/n, −1, 1]).</summary>
    private sealed class TransformerC
    {
        private readonly int[] _nodes;
        private readonly double[] _a;
        private readonly double _g, _ciTr, _ciBe;
        private readonly int _slotS, _slotP;
        private double _i, _ih;

        public TransformerC(EmtTransformer t, int[] nodes, double dt, int slotS, int slotP)
        {
            if (!(t.Ratio > 0)) throw new ArgumentException($"Transformer '{t.Name}': ratio must be positive.");
            if (t.R < 0 || t.L < 0 || t.R + t.L <= 0) throw new ArgumentException($"Transformer '{t.Name}': leakage R + L must be positive.");
            _nodes = nodes;
            _a = [1 / t.Ratio, -1 / t.Ratio, -1, 1];
            _g = 1 / (t.R + 2 * t.L / dt);
            _ciTr = _g * (2 * t.L / dt - t.R);
            _ciBe = _g * (2 * t.L / dt);
            _slotS = slotS; _slotP = slotP;
        }

        public void Stamp(List<(int, int, double)> c)
        {
            for (int p = 0; p < 4; p++)
                for (int q = 0; q < 4; q++)
                    if (_nodes[p] >= 0 && _nodes[q] >= 0) c.Add((_nodes[p], _nodes[q], _g * _a[p] * _a[q]));
        }

        public void Update(double[] v, bool nextBe, double[] hist, double[] cur)
        {
            double u = 0;
            for (int p = 0; p < 4; p++) if (_nodes[p] >= 0) u += _a[p] * v[_nodes[p]];
            _i = _g * u + _ih;
            _ih = nextBe ? _ciBe * _i : _g * u + _ciTr * _i;
            for (int p = 0; p < 4; p++) if (_nodes[p] >= 0) hist[_nodes[p]] -= _a[p] * _ih;
            if (_slotS >= 0) cur[_slotS] = _i;
            if (_slotP >= 0) cur[_slotP] = _i * _a[0];
        }
    }

    /// <summary>Multiphase Bergeron line with modal decomposition and lumped losses (R/4 at the ends, R/2 in the middle per mode).</summary>
    private sealed class LineC
    {
        public readonly int N;
        public readonly int[] K, M;
        public readonly double[] Gph;
        private readonly double[] _t, _tinv;     // n×n
        private readonly double[] _z, _h, _tau;  // per mode; _tau in steps
        private readonly double[][] _vk, _ik, _vm, _im; // circular buffers per mode
        private readonly int _len;
        private int _count;                      // number of stored full steps (step s at s % len)
        private readonly double[] _ihK, _ihM, _mk, _mm, _tmp;
        private readonly int[] _slotK, _slotM;
        private readonly double[] _iK, _iM;

        public LineC(BergeronLine l, int[] k, int[] m, double dt, Func<int, int> slotK, Func<int, int> slotM)
        {
            N = k.Length;
            if (N == 0 || m.Length != N || l.Modes.Length != N || l.Transformation.GetLength(0) != N || l.Transformation.GetLength(1) != N)
                throw new ArgumentException($"Line '{l.Name}': terminal, mode and transformation dimensions must agree.");
            K = k; M = m;
            _t = new double[N * N];
            for (int p = 0; p < N; p++) for (int q = 0; q < N; q++) _t[p * N + q] = l.Transformation[p, q];
            _tinv = Dense.Inverse(_t, N, l.Name);
            _z = new double[N]; _h = new double[N]; _tau = new double[N];
            double maxTau = 0;
            for (int j = 0; j < N; j++)
            {
                var md = l.Modes[j];
                if (!(md.SurgeImpedance > 0) || md.Resistance < 0) throw new ArgumentException($"Line '{l.Name}': invalid modal data.");
                if (md.TravelTime < dt * (1 - 1e-9))
                    throw new ArgumentException($"Line '{l.Name}' mode {j}: travel time {md.TravelTime:g4} s is shorter than the time step {dt:g4} s; use a π section or a smaller Δt.");
                _z[j] = md.SurgeImpedance + md.Resistance / 4;
                _h[j] = (md.SurgeImpedance - md.Resistance / 4) / (md.SurgeImpedance + md.Resistance / 4);
                _tau[j] = md.TravelTime / dt;
                maxTau = Math.Max(maxTau, _tau[j]);
            }
            Gph = new double[N * N];
            for (int p = 0; p < N; p++)
                for (int q = 0; q < N; q++)
                {
                    double s = 0;
                    for (int j = 0; j < N; j++) s += _t[p * N + j] / _z[j] * _tinv[j * N + q];
                    Gph[p * N + q] = s;
                }
            _len = (int)Math.Ceiling(maxTau) + 3;
            _vk = Alloc(); _ik = Alloc(); _vm = Alloc(); _im = Alloc();
            _ihK = new double[N]; _ihM = new double[N]; _mk = new double[N]; _mm = new double[N]; _tmp = new double[N];
            _iK = new double[N]; _iM = new double[N];
            _slotK = Enumerable.Range(0, N).Select(slotK).ToArray();
            _slotM = Enumerable.Range(0, N).Select(slotM).ToArray();

            double[][] Alloc() { var a = new double[N][]; for (int j = 0; j < N; j++) a[j] = new double[_len]; return a; }
        }

        /// <summary>De-energized initial state: the t = 0 sample is zero.</summary>
        public void Reset() { _count = 1; foreach (var b in new[] { _vk, _ik, _vm, _im }) foreach (var x in b) Array.Clear(x); }

        private double Delayed(double[] buf, double p)
        {
            if (p <= 0) return 0;
            int i0 = (int)p;
            double f = p - i0;
            double a = buf[i0 % _len];
            if (f == 0 || i0 + 1 >= _count) return a;
            return a + f * (buf[(i0 + 1) % _len] - a);
        }

        /// <summary>History currents at step position <paramref name="pos"/> (in units of Δt) added to the nodal source vector.</summary>
        public void History(double pos, double[] b)
        {
            for (int j = 0; j < N; j++)
            {
                double p = pos - _tau[j];
                double vk = Delayed(_vk[j], p), ik = Delayed(_ik[j], p), vm = Delayed(_vm[j], p), im = Delayed(_im[j], p);
                double h = _h[j], z = _z[j];
                double a = 0.5 * (1 + h), c = 0.5 * (1 - h);
                _mk[j] = -a * (vm / z + h * im) - c * (vk / z + h * ik);
                _mm[j] = -a * (vk / z + h * ik) - c * (vm / z + h * im);
            }
            for (int p = 0; p < N; p++)
            {
                double sk = 0, sm = 0;
                for (int j = 0; j < N; j++) { sk += _t[p * N + j] * _mk[j]; sm += _t[p * N + j] * _mm[j]; }
                _ihK[p] = sk; _ihM[p] = sm;
                if (K[p] >= 0) b[K[p]] -= sk;
                if (M[p] >= 0) b[M[p]] -= sm;
            }
        }

        /// <summary>Terminal currents after the solution; full steps are pushed into the modal history buffers.</summary>
        public void Terminal(double[] v, bool fullStep, double[] cur)
        {
            for (int p = 0; p < N; p++)
            {
                double sk = _ihK[p], sm = _ihM[p];
                for (int q = 0; q < N; q++)
                {
                    double g = Gph[p * N + q];
                    sk += g * (K[q] >= 0 ? v[K[q]] : 0.0);
                    sm += g * (M[q] >= 0 ? v[M[q]] : 0.0);
                }
                _iK[p] = sk; _iM[p] = sm;
                if (_slotK[p] >= 0) cur[_slotK[p]] = sk;
                if (_slotM[p] >= 0) cur[_slotM[p]] = sm;
            }
            if (!fullStep) return;
            int slot = _count % _len;
            Push(_vk, K, v, slot, true); Push(_ik, null, _iK, slot, false);
            Push(_vm, M, v, slot, true); Push(_im, null, _iM, slot, false);
            _count++;
        }

        private void Push(double[][] buf, int[]? nodes, double[] src, int slot, bool voltage)
        {
            for (int p = 0; p < N; p++) _tmp[p] = voltage ? (nodes![p] >= 0 ? src[nodes[p]] : 0.0) : src[p];
            for (int j = 0; j < N; j++)
            {
                double s = 0;
                for (int p = 0; p < N; p++) s += _tinv[j * N + p] * _tmp[p];
                buf[j][slot] = s;
            }
        }
    }

    private static class Dense
    {
        public static double[] Mul(double[] a, double[] b, int n)
        {
            var r = new double[n * n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                {
                    double x = a[i * n + k];
                    for (int j = 0; j < n; j++) r[i * n + j] += x * b[k * n + j];
                }
            return r;
        }

        public static double[] Inverse(double[] m, int n, string name)
        {
            var a = (double[])m.Clone();
            var inv = new double[n * n];
            for (int i = 0; i < n; i++) inv[i * n + i] = 1;
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(a[r * n + c]) > Math.Abs(a[piv * n + c])) piv = r;
                if (Math.Abs(a[piv * n + c]) < 1e-300) throw new ArgumentException($"Element '{name}': singular matrix.");
                if (piv != c)
                    for (int j = 0; j < n; j++)
                    {
                        (a[c * n + j], a[piv * n + j]) = (a[piv * n + j], a[c * n + j]);
                        (inv[c * n + j], inv[piv * n + j]) = (inv[piv * n + j], inv[c * n + j]);
                    }
                double d = a[c * n + c];
                for (int j = 0; j < n; j++) { a[c * n + j] /= d; inv[c * n + j] /= d; }
                for (int r = 0; r < n; r++)
                {
                    if (r == c) continue;
                    double f = a[r * n + c];
                    if (f == 0) continue;
                    for (int j = 0; j < n; j++) { a[r * n + j] -= f * a[c * n + j]; inv[r * n + j] -= f * inv[c * n + j]; }
                }
            }
            return inv;
        }
    }
}
