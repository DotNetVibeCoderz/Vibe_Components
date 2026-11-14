using System.Diagnostics;
using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Dynamics;

/// <summary>Time integration scheme.</summary>
public enum IntegrationMethod
{
    /// <summary>
    /// Simultaneous implicit trapezoidal rule: Newton on the combined system [x; V] with a sparse Jacobian built from exact network
    /// admittances and numerical per-unit derivatives, reused across steps (refreshed after events, step changes or slow convergence).
    /// </summary>
    ImplicitTrapezoidal,
    /// <summary>Partitioned explicit modified Euler (Heun / RK2) with a network solution at the predictor and the corrector.</summary>
    ModifiedEuler,
}

/// <summary>Time-domain simulation options.</summary>
public sealed record SimulationOptions
{
    /// <summary>End time [s].</summary>
    public double EndTimeS { get; init; } = 10;
    /// <summary>Step [s]; 0 = half a cycle of the network frequency (0.01 s at 50 Hz, 0.008333 s at 60 Hz).</summary>
    public double TimeStepS { get; init; }
    public IntegrationMethod Method { get; init; } = IntegrationMethod.ImplicitTrapezoidal;
    /// <summary>Record every n-th step.</summary>
    public int OutputInterval { get; init; } = 1;
    /// <summary>Instability threshold on the rotor-angle deviation from the centre of inertia [degrees].</summary>
    public double InstabilityAngleDeg { get; init; } = 180;
    public bool StopWhenUnstable { get; init; }
    /// <summary>Additional state channels by qualified name or unique suffix (e.g. "G1.GENROU.Eq'").</summary>
    public IReadOnlyList<string> StateChannels { get; init; } = [];
    /// <summary>Record bus voltage magnitudes and frequencies.</summary>
    public bool RecordBuses { get; init; } = true;
    public int MaxNewtonIterations { get; init; } = 15;
    /// <summary>Newton tolerance on the combined residual [pu].</summary>
    public double NewtonTolerance { get; init; } = 1e-8;
    /// <summary>Network iteration tolerance [pu].</summary>
    public double NetworkTolerance { get; init; } = 1e-10;

    public static SimulationOptions Default { get; } = new();
}

/// <summary>
/// Channels of a dynamic simulation. Per-unit arrays are indexed [unit][time]: angles in degrees, speed in pu, voltages in pu, P in MW,
/// Q in Mvar, Efd in pu, Pm in pu on the machine base, converter currents in pu on the converter base.
/// </summary>
public sealed class DynamicSimulationResult
{
    public required double[] Time { get; init; }
    public required IReadOnlyList<string> UnitNames { get; init; }
    /// <summary>Internal / rotor angle in the network frame [degrees] (NaN for units without angle).</summary>
    public required double[][] AngleDeg { get; init; }
    /// <summary>Angle relative to the centre of inertia (or to the infinite bus) [degrees].</summary>
    public required double[][] AngleCoiDeg { get; init; }
    public required double[][] SpeedPu { get; init; }
    public required double[][] TerminalVoltagePu { get; init; }
    public required double[][] ActivePowerMw { get; init; }
    public required double[][] ReactivePowerMvar { get; init; }
    public required double[][] FieldVoltagePu { get; init; }
    public required double[][] MechanicalPowerPu { get; init; }
    public required double[][] ActiveCurrentPu { get; init; }
    public required double[][] ReactiveCurrentPu { get; init; }
    /// <summary>Bus voltage magnitudes [pu], indexed [bus position][time] (empty when not recorded).</summary>
    public required double[][] BusVoltagePu { get; init; }
    /// <summary>Bus frequency from the voltage-angle derivative [Hz] (empty when not recorded).</summary>
    public required double[][] BusFrequencyHz { get; init; }
    /// <summary>Requested state channels.</summary>
    public required IReadOnlyDictionary<string, double[]> States { get; init; }
    public required bool Stable { get; init; }
    public required double MaxAngleDeviationDeg { get; init; }
    public required int Steps { get; init; }
    /// <summary>Total Newton iterations (implicit) or network iterations (explicit).</summary>
    public required int Iterations { get; init; }
    /// <summary>Network solutions that did not reach the tolerance (results after such a step are suspect).</summary>
    public required int ConvergenceFailures { get; init; }
    /// <summary>Implicit steps whose Newton iteration did not converge and were redone with two explicit half steps.</summary>
    public int FallbackSteps { get; internal set; }
    /// <summary>Wall-clock time of the run.</summary>
    public TimeSpan Elapsed { get; internal set; }
    public required double[] FinalState { get; init; }

    /// <summary>Index of a unit by name (case-insensitive).</summary>
    public int UnitIndex(string name)
    {
        for (int i = 0; i < UnitNames.Count; i++) if (string.Equals(UnitNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new KeyNotFoundException($"No unit '{name}'.");
    }

    /// <summary>Channel by name: "ANGLE G1", "ANGLECOI G1", "SPEED G1", "VT G1", "P G1", "Q G1", "EFD G1", "PM G1", "IP G1", "IQ G1",
    /// "VBUS 7", "FBUS 7" or a requested state name.</summary>
    public double[] Channel(string name)
    {
        if (States.TryGetValue(name, out var s)) return s;
        var parts = name.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            string k = parts[0].ToUpperInvariant();
            if (k is "VBUS" or "FBUS") throw new ArgumentException("Use BusVoltagePu / BusFrequencyHz with a bus position.");
            int u = UnitIndex(parts[1]);
            return k switch
            {
                "ANGLE" => AngleDeg[u], "ANGLECOI" => AngleCoiDeg[u], "SPEED" => SpeedPu[u], "VT" => TerminalVoltagePu[u],
                "P" => ActivePowerMw[u], "Q" => ReactivePowerMvar[u], "EFD" => FieldVoltagePu[u], "PM" => MechanicalPowerPu[u],
                "IP" => ActiveCurrentPu[u], "IQ" => ReactiveCurrentPu[u],
                _ => throw new KeyNotFoundException($"Unknown channel '{name}'."),
            };
        }
        throw new KeyNotFoundException($"Unknown channel '{name}'.");
    }

    /// <summary>Value of a channel at time <paramref name="t"/> [s] (nearest recorded sample at or before t).</summary>
    public double At(double[] channel, double t)
    {
        int k = Array.BinarySearch(Time, t + 1e-9);
        if (k < 0) k = ~k - 1;
        return channel[Math.Clamp(k, 0, channel.Length - 1)];
    }
}

/// <summary>Time-domain (RMS) simulation of a <see cref="DynamicSystem"/>.</summary>
public static class DynamicSimulation
{
    /// <summary>Runs a simulation from the initial equilibrium with the given events. The system configuration is reset first.</summary>
    public static DynamicSimulationResult Run(DynamicSystem system, IEnumerable<DynamicEvent>? events = null, SimulationOptions? options = null)
    {
        var opt = options ?? SimulationOptions.Default;
        var sw = Stopwatch.StartNew();
        system.Reset();
        var sim = new Runner(system, opt);
        var ev = (events ?? []).OrderBy(e => e.TimeS).ToList();
        var r = sim.Run(ev);
        system.Reset();
        r.Elapsed = sw.Elapsed;
        return r;
    }

    private sealed class Runner
    {
        private readonly DynamicSystem _sys;
        private readonly SimulationOptions _o;
        private readonly int _n, _nb;
        private readonly double[] _x, _f, _fn;
        private readonly Complex[] _v;
        private readonly Complex[] _inj;
        private readonly Recorder _rec;
        private int _iterations, _failures, _fallbacks;
        // implicit Jacobian cache
        private SparseLU<double>? _jlu;
        private int[]? _jOrder;
        private int _jVersion = -1;
        private double _jStep;

        public Runner(DynamicSystem sys, SimulationOptions o)
        {
            _sys = sys; _o = o; _n = sys.StateCount; _nb = sys.BusCount;
            _x = (double[])sys.InitialState.Clone();
            _v = (Complex[])sys.InitialVoltages.Clone();
            _f = new double[_n]; _fn = new double[_n];
            _inj = new Complex[_nb];
            _rec = new Recorder(sys, o);
        }

        public DynamicSimulationResult Run(List<DynamicEvent> ev)
        {
            double h0 = _o.TimeStepS > 0 ? _o.TimeStepS : 1 / (2 * _sys.FrequencyHz);
            double t = 0;
            int ei = 0;
            ApplyEvents(ev, ref ei, t);
            _sys.Derivatives(t, _x, _v, _fn);
            _rec.Record(t, _x, _v);
            int step = 0;
            bool stable = true;
            while (t < _o.EndTimeS - 1e-9)
            {
                double h = Math.Min(h0, _o.EndTimeS - t);
                if (ei < ev.Count && ev[ei].TimeS < t + h - 1e-9 && ev[ei].TimeS > t + 1e-9) h = ev[ei].TimeS - t;
                if (_o.Method == IntegrationMethod.ModifiedEuler) StepEuler(t, h); else StepTrapezoidal(t, h);
                t += h;
                step++;
                _sys.AfterStep(t, _x);
                if (ApplyEvents(ev, ref ei, t)) _sys.Derivatives(t, _x, _v, _fn);
                bool rec = step % Math.Max(1, _o.OutputInterval) == 0 || t >= _o.EndTimeS - 1e-9;
                double dev = _rec.Deviation(_x);
                if (dev > _o.InstabilityAngleDeg && stable)
                {
                    stable = false;
                    if (_o.StopWhenUnstable) { _rec.Record(t, _x, _v); break; }
                }
                if (rec) _rec.Record(t, _x, _v);
            }
            var res = _rec.Result(stable, step, _iterations, _failures, (double[])_x.Clone());
            res.FallbackSteps = _fallbacks;
            return res;
        }

        private bool ApplyEvents(List<DynamicEvent> ev, ref int ei, double t)
        {
            bool any = false;
            while (ei < ev.Count && ev[ei].TimeS <= t + 1e-9) { _sys.Apply(ev[ei++]); any = true; }
            if (any)
            {
                int it = _sys.SolveNetwork(_x, _v, _o.NetworkTolerance);
                if (it < 0) _failures++;
            }
            return any;
        }

        // ---------------- explicit partitioned modified Euler
        private void StepEuler(double t, double h)
        {
            var x1 = new double[_n];
            // _fn holds f(t, x, V) consistent with the current network solution
            for (int i = 0; i < _n; i++) x1[i] = _x[i] + h * _fn[i];
            _sys.ApplyLimits(x1);
            var v1 = (Complex[])_v.Clone();
            Count(_sys.SolveNetwork(x1, v1, _o.NetworkTolerance));
            _sys.Derivatives(t + h, x1, v1, _f);
            for (int i = 0; i < _n; i++) _x[i] += 0.5 * h * (_fn[i] + _f[i]);
            _sys.ApplyLimits(_x);
            Array.Copy(v1, _v, _nb);
            Count(_sys.SolveNetwork(_x, _v, _o.NetworkTolerance));
            _sys.Derivatives(t + h, _x, _v, _fn);
        }

        private void Count(int it) { if (it < 0) _failures++; else _iterations += it; }

        // ---------------- implicit trapezoidal, simultaneous
        private double[] Residual(double t, double h, double[] xk, Complex[] vk, double[] xn, double[] r)
        {
            _sys.Derivatives(t + h, xk, vk, _f);
            for (int i = 0; i < _n; i++) r[i] = xk[i] - xn[i] - 0.5 * h * (_f[i] + _fn[i]);
            _sys.NetworkResidual(xk, vk, _inj, r, _n);
            return r;
        }

        private void StepTrapezoidal(double t, double h)
        {
            var xn = (double[])_x.Clone();
            var xk = (double[])_x.Clone();
            var vk = (Complex[])_v.Clone();
            // explicit predictor for the states
            for (int i = 0; i < _n; i++) xk[i] += h * _fn[i];
            var r = new double[_n + 2 * _nb];
            bool fresh = false;
            if (_jlu is null || _jVersion != _sys.ConfigurationVersion || Math.Abs(_jStep - h) > 1e-12)
            {
                BuildJacobian(t, h, xk, vk); fresh = true;
            }
            bool ok = false;
            double prev = double.PositiveInfinity;
            var clamped = new bool[_n];
            var before = new double[_n];
            bool anyClamp = false;
            for (int it = 1; it <= _o.MaxNewtonIterations; it++)
            {
                Residual(t, h, xk, vk, xn, r);
                // states held on a non-windup limit satisfy their (inactive) equation
                if (anyClamp) for (int i = 0; i < _n; i++) if (clamped[i]) r[i] = 0;
                double nr = 0;
                for (int i = 0; i < r.Length; i++) nr = Math.Max(nr, Math.Abs(r[i]));
                _iterations++;
                if (nr < _o.NewtonTolerance) { ok = true; break; }
                if (!fresh && (it > 4 || nr > 0.5 * prev && it > 1))
                {
                    BuildJacobian(t, h, xk, vk); fresh = true;
                }
                prev = nr;
                var dz = _jlu!.Solve(r);
                for (int i = 0; i < _n; i++) xk[i] -= dz[i];
                for (int b = 0; b < _nb; b++) vk[b] -= new Complex(dz[_n + 2 * b], dz[_n + 2 * b + 1]);
                Array.Copy(xk, before, _n);
                _sys.ApplyLimits(xk);
                for (int i = 0; i < _n; i++) if (xk[i] != before[i]) { clamped[i] = true; anyClamp = true; }
            }
            if (!ok)
            {
                // non-smooth switching (limiters, current limits) can stall Newton: redo the step with two explicit half steps
                _fallbacks++;
                StepEuler(t, 0.5 * h);
                StepEuler(t + 0.5 * h, 0.5 * h);
                return;
            }
            Array.Copy(xk, _x, _n);
            Array.Copy(vk, _v, _nb);
            _sys.Derivatives(t + h, _x, _v, _fn);
        }

        /// <summary>
        /// J = [[I − h/2·∂f/∂x, −h/2·∂f/∂V], [∂g/∂x, ∂g/∂V]] with per-unit forward differences (a unit only depends on its own states
        /// and its bus voltage), exact −Y and swapped network rows.
        /// </summary>
        private void BuildJacobian(double t, double h, double[] x, Complex[] v)
        {
            int n = _n, m = _n + 2 * _nb;
            var b = new SparseBuilder<double>(m, m);
            foreach (var u in _sys.Units)
            {
                int o = u.StateOffset, k = u.StateCount, bus = u.BusIndex;
                if (!u.InService)
                {
                    for (int i = 0; i < k; i++) b.Add(o + i, o + i, 1);
                    continue;
                }
                var xs = x.AsSpan(o, k).ToArray();
                var f0 = new double[k]; var f1 = new double[k];
                var vb = v[bus];
                u.Derivatives(t + h, xs, vb, f0);
                var i0 = u.Injection(xs, vb);
                int rowRe = n + 2 * bus + 1, rowIm = n + 2 * bus;
                for (int j = 0; j < k; j++)
                {
                    double eps = 1e-7 * Math.Max(1, Math.Abs(xs[j]));
                    double old = xs[j];
                    xs[j] = old + eps;
                    u.Derivatives(t + h, xs, vb, f1);
                    var i1 = u.Injection(xs, vb);
                    xs[j] = old;
                    for (int i = 0; i < k; i++) b.Add(o + i, o + j, (i == j ? 1 : 0) - 0.5 * h * (f1[i] - f0[i]) / eps);
                    var di = (i1 - i0) / eps;
                    b.Add(rowIm, o + j, di.Imaginary);
                    b.Add(rowRe, o + j, di.Real);
                }
                const double ev = 1e-7;
                for (int c = 0; c < 2; c++)
                {
                    var vp = vb + (c == 0 ? new Complex(ev, 0) : new Complex(0, ev));
                    u.Derivatives(t + h, xs, vp, f1);
                    for (int i = 0; i < k; i++) b.Add(o + i, n + 2 * bus + c, -0.5 * h * (f1[i] - f0[i]) / ev);
                }
                u.Derivatives(t + h, xs, vb, f0); // restore outputs / discrete memory at the base point
            }
            _sys.StampNetwork(b, n, x, v);
            var jac = b.ToCsr();
            _jlu = SparseLU<double>.Factorize(jac, _jOrder);
            _jOrder ??= _jlu.Ordering;
            _jVersion = _sys.ConfigurationVersion;
            _jStep = h;
        }
    }

    private sealed class Recorder
    {
        private readonly DynamicSystem _sys;
        private readonly SimulationOptions _o;
        private readonly List<double> _t = [];
        private readonly List<double[]> _ang = [], _coi = [], _spd = [], _vt = [], _p = [], _q = [], _efd = [], _pm = [], _ip = [], _iq = [], _vb = [], _fb = [];
        private readonly List<double[]> _st = [];
        private readonly int[] _stIdx;
        private readonly string[] _stNames;
        private double[]? _prevAngle;
        private double _prevT;
        private double _maxDev;

        public Recorder(DynamicSystem sys, SimulationOptions o)
        {
            _sys = sys; _o = o;
            _stNames = o.StateChannels.ToArray();
            _stIdx = _stNames.Select(s => sys.StateIndex(s) is var k && k >= 0 ? k : throw new ArgumentException($"Unknown state channel '{s}'.")).ToArray();
        }

        private double Coi(ReadOnlySpan<double> x)
        {
            double sw = 0, sa = 0;
            foreach (var u in _sys.Units)
            {
                if (!u.InService || u is not SynchronousMachineUnit mu) continue;
                double a = x[u.StateOffset + mu.Machine.AngleState];
                if (u.IsInfiniteBus) return a;
                sw += u.InertiaWeight; sa += u.InertiaWeight * a;
            }
            return sw > 0 ? sa / sw : 0;
        }

        public double Deviation(ReadOnlySpan<double> x)
        {
            double c = Coi(x), d = 0;
            foreach (var u in _sys.Units)
                if (u.InService && u is SynchronousMachineUnit mu) d = Math.Max(d, Math.Abs(x[u.StateOffset + mu.Machine.AngleState] - c));
            d *= Scalar.Rad2Deg;
            _maxDev = Math.Max(_maxDev, d);
            return d;
        }

        public void Record(double t, double[] x, Complex[] v)
        {
            int m = _sys.Units.Count;
            var dx = new double[_sys.StateCount];
            _sys.Derivatives(t, x, v, dx); // refresh outputs
            double c = Coi(x);
            double[] A() => new double[m];
            double[] ang = A(), coi = A(), spd = A(), vt = A(), p = A(), q = A(), efd = A(), pm = A(), ip = A(), iq = A();
            double sb = _sys.SystemBase;
            for (int k = 0; k < m; k++)
            {
                var u = _sys.Units[k];
                var o = u.Outputs;
                if (!u.InService)
                {
                    ang[k] = coi[k] = spd[k] = efd[k] = pm[k] = ip[k] = iq[k] = double.NaN; vt[k] = v[u.BusIndex].Magnitude; p[k] = q[k] = 0;
                    continue;
                }
                ang[k] = o.AngleRad * Scalar.Rad2Deg;
                coi[k] = u is SynchronousMachineUnit ? (o.AngleRad - c) * Scalar.Rad2Deg : double.NaN;
                spd[k] = o.Speed; vt[k] = o.Vt; p[k] = o.P * sb; q[k] = o.Q * sb; efd[k] = o.Efd; pm[k] = o.Pm; ip[k] = o.Ip; iq[k] = o.Iq;
            }
            _t.Add(t); _ang.Add(ang); _coi.Add(coi); _spd.Add(spd); _vt.Add(vt); _p.Add(p); _q.Add(q); _efd.Add(efd); _pm.Add(pm); _ip.Add(ip); _iq.Add(iq);
            if (_o.RecordBuses)
            {
                int nb = _sys.BusCount;
                var vm = new double[nb]; var fr = new double[nb]; var an = new double[nb];
                for (int i = 0; i < nb; i++)
                {
                    vm[i] = v[i].Magnitude; an[i] = v[i].Phase;
                    double f = _sys.FrequencyHz;
                    if (_prevAngle is not null && t > _prevT && vm[i] > 1e-3)
                        f += Blocks.WrapAngle(an[i] - _prevAngle[i]) / (t - _prevT) / (2 * Math.PI);
                    fr[i] = f;
                }
                _vb.Add(vm); _fb.Add(fr);
                _prevAngle = an; _prevT = t;
            }
            if (_stIdx.Length > 0) _st.Add(_stIdx.Select(i => x[i]).ToArray());
        }

        public DynamicSimulationResult Result(bool stable, int steps, int iterations, int failures, double[] final)
        {
            int m = _sys.Units.Count;
            double[][] T(List<double[]> rows, int w) => Enumerable.Range(0, w).Select(i => rows.Select(r => r[i]).ToArray()).ToArray();
            var states = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < _stNames.Length; k++) states[_stNames[k]] = _st.Select(r => r[k]).ToArray();
            int nb = _o.RecordBuses ? _sys.BusCount : 0;
            return new DynamicSimulationResult
            {
                Time = _t.ToArray(), UnitNames = _sys.Units.Select(u => u.Name).ToArray(),
                AngleDeg = T(_ang, m), AngleCoiDeg = T(_coi, m), SpeedPu = T(_spd, m), TerminalVoltagePu = T(_vt, m), ActivePowerMw = T(_p, m),
                ReactivePowerMvar = T(_q, m), FieldVoltagePu = T(_efd, m), MechanicalPowerPu = T(_pm, m), ActiveCurrentPu = T(_ip, m),
                ReactiveCurrentPu = T(_iq, m), BusVoltagePu = T(_vb, nb), BusFrequencyHz = T(_fb, nb), States = states,
                Stable = stable, MaxAngleDeviationDeg = _maxDev, Steps = steps, Iterations = iterations, ConvergenceFailures = failures,
                FinalState = final,
            };
        }
    }
}
