using System.Numerics;
using Electrical.Core.Network;
using Electrical.Pmu.C37118;
using Electrical.PowerFlow;

namespace Electrical.Pmu;

/// <summary>Branch terminal of a current phasor.</summary>
public enum BranchTerminal { From, To }

/// <summary>
/// What a phasor channel measures in the network: the positive-sequence voltage of <paramref name="BusId"/>, or the current
/// leaving <paramref name="BusId"/> into branch <paramref name="BranchIndex"/> (position in <see cref="PowerNetwork.Branches"/>) at
/// terminal <paramref name="End"/>.
/// </summary>
public sealed record PmuChannelBinding(PhasorKind Kind, int BusId, int BranchIndex = -1, BranchTerminal End = BranchTerminal.From);

/// <summary>Options of a <see cref="PmuSimulator"/>.</summary>
public sealed record PmuSimulatorOptions
{
    /// <summary>IDCODE of the output stream (a PDC-style stream with one block per PMU bus).</summary>
    public ushort StreamIdCode { get; init; } = 1;
    /// <summary>Reporting rate [frames/s].</summary>
    public short DataRate { get; init; } = 50;
    public C37DataFormat Format { get; init; } = C37DataFormat.FloatPolar;
    /// <summary>Nominal frequency [Hz] (default: the network's <see cref="PowerNetwork.FrequencyHz"/>).</summary>
    public double? NominalFrequencyHz { get; init; }
    /// <summary>Constant off-nominal deviation [Hz]; phasor angles rotate at 2π·Δf.</summary>
    public double FrequencyOffsetHz { get; init; }
    /// <summary>Constant rate of change of frequency [Hz/s] (applied from <see cref="ReferenceTime"/>).</summary>
    public double RocofHzPerS { get; init; }
    /// <summary>Instant at which the phasor angles equal the power-flow angles.</summary>
    public DateTimeOffset ReferenceTime { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    /// <summary>σ of the relative magnitude noise of voltage phasors [pu of magnitude].</summary>
    public double VoltageNoise { get; init; }
    /// <summary>σ of the relative magnitude noise of current phasors [pu of magnitude].</summary>
    public double CurrentNoise { get; init; }
    /// <summary>σ of the phase-angle noise [degrees].</summary>
    public double AngleNoiseDeg { get; init; }
    /// <summary>σ of the frequency noise [Hz].</summary>
    public double FrequencyNoiseHz { get; init; }
    /// <summary>Report current phasors of all in-service branches incident to a PMU bus.</summary>
    public bool IncludeCurrents { get; init; } = true;
    /// <summary>Seed of the deterministic Gaussian noise.</summary>
    public int Seed { get; init; } = 2026;
}

/// <summary>
/// PMU simulator: publishes a solved <see cref="PowerFlowResult"/> as C37.118 synchrophasors. Each PMU bus reports its positive-sequence
/// voltage [V line-to-neutral RMS] and the currents [A] of its incident branches, at nominal ± <see cref="PmuSimulatorOptions.FrequencyOffsetHz"/>
/// with deterministic noise. Serve it with <see cref="PmuServer"/>.
/// </summary>
public sealed class PmuSimulator : IPmuDataSource
{
    private readonly Random _rng;
    private readonly Lock _gate = new();
    private readonly Complex[][] _exact;
    private readonly double _fnom;

    /// <param name="pmuBuses">Bus ids equipped with a PMU (default: every bus).</param>
    public PmuSimulator(PowerFlowResult result, IEnumerable<int>? pmuBuses = null, PmuSimulatorOptions? options = null)
    {
        Result = result;
        Options = options ?? new PmuSimulatorOptions();
        var net = result.Network;
        _fnom = Options.NominalFrequencyHz ?? net.FrequencyHz;
        _rng = new Random(Options.Seed);
        var buses = (pmuBuses ?? net.Buses.Select(b => b.Id)).ToArray();
        var y = AdmittanceMatrix.Build(net);
        var bindings = new List<IReadOnlyList<PmuChannelBinding>>();
        var pmus = new List<PmuConfiguration>();
        _exact = new Complex[buses.Length][];
        for (int p = 0; p < buses.Length; p++)
        {
            int busId = buses[p];
            var bus = net.GetBus(busId);
            var b = new List<PmuChannelBinding> { new(PhasorKind.Voltage, busId) };
            var ch = new List<PhasorChannel> { new($"V_B{busId}", PhasorKind.Voltage, ScaleFor(bus.BaseKv * 1000 / Math.Sqrt(3) * 1.5)) };
            var x = new List<Complex> { result.Voltages[net.IndexOf(busId)] * (bus.BaseKv * 1000 / Math.Sqrt(3)) };
            if (Options.IncludeCurrents)
            {
                double ibase = net.BaseMva * 1e6 / (Math.Sqrt(3) * bus.BaseKv * 1000);
                for (int k = 0; k < net.Branches.Count; k++)
                {
                    var br = net.Branches[k];
                    if (!br.InService || (br.From != busId && br.To != busId)) continue;
                    var end = br.From == busId ? BranchTerminal.From : BranchTerminal.To;
                    var ipu = BranchCurrent(net, y, result.Voltages, k, end);
                    b.Add(new PmuChannelBinding(PhasorKind.Current, busId, k, end));
                    ch.Add(new PhasorChannel($"I_BR{k}{(end == BranchTerminal.From ? "F" : "T")}", PhasorKind.Current, ScaleFor(Math.Max(ipu.Magnitude * ibase * 2, ibase))));
                    x.Add(ipu * ibase);
                }
            }
            bindings.Add(b);
            _exact[p] = [.. x];
            pmus.Add(new PmuConfiguration
            {
                StationName = bus.Name is { Length: > 0 } n ? n : $"BUS {busId}",
                IdCode = (ushort)busId,
                Format = Options.Format,
                Phasors = ch,
                NominalFrequencyHz = _fnom,
            });
        }
        Bindings = bindings;
        Configuration = new C37ConfigFrame { IdCode = Options.StreamIdCode, Pmus = pmus, DataRate = Options.DataRate };
        HeaderText = $"Electrical.Net PMU simulator: {net.Name}, {buses.Length} PMUs, {_fnom} Hz nominal, {Options.DataRate} frames/s";
    }

    public PowerFlowResult Result { get; }
    public PmuSimulatorOptions Options { get; }
    public C37ConfigFrame Configuration { get; }
    public string HeaderText { get; }
    /// <summary>Network meaning of every phasor, indexed [block][phasor] like the configuration.</summary>
    public IReadOnlyList<IReadOnlyList<PmuChannelBinding>> Bindings { get; }

    /// <summary>Branch current [pu] leaving the bus at terminal <paramref name="end"/>.</summary>
    internal static Complex BranchCurrent(PowerNetwork net, AdmittanceMatrix y, IReadOnlyList<Complex> v, int branch, BranchTerminal end)
    {
        var br = net.Branches[branch];
        var a = y.Branches[branch];
        var vf = v[net.IndexOf(br.From)];
        var vt = v[net.IndexOf(br.To)];
        return end == BranchTerminal.From ? a.Yff * vf + a.Yft * vt : a.Ytf * vf + a.Ytt * vt;
    }

    /// <summary>PHUNIT scale [10⁻⁵ units/bit] so that <paramref name="fullScale"/> fits in 16-bit integer data.</summary>
    private static uint ScaleFor(double fullScale) => (uint)Math.Clamp(Math.Ceiling(fullScale / 32767 * 1e5), 1, 0xFFFFFF);

    public C37DataFrame CreateFrame(DateTimeOffset timestamp)
    {
        double dt = (timestamp - Options.ReferenceTime).TotalSeconds;
        double df = Options.FrequencyOffsetHz + Options.RocofHzPerS * dt;
        double rot = 2 * Math.PI * (Options.FrequencyOffsetHz * dt + 0.5 * Options.RocofHzPerS * dt * dt);
        var blocks = new PmuDataBlock[_exact.Length];
        lock (_gate)
        {
            for (int p = 0; p < _exact.Length; p++)
            {
                var ph = new Complex[_exact[p].Length];
                for (int i = 0; i < ph.Length; i++)
                {
                    bool v = Bindings[p][i].Kind == PhasorKind.Voltage;
                    double mag = _exact[p][i].Magnitude * (1 + (v ? Options.VoltageNoise : Options.CurrentNoise) * Gauss());
                    double ang = _exact[p][i].Phase + rot + Options.AngleNoiseDeg * Math.PI / 180 * Gauss();
                    ph[i] = Complex.FromPolarCoordinates(mag, ang);
                }
                blocks[p] = new PmuDataBlock(PmuStatus.None, ph, _fnom + df + Options.FrequencyNoiseHz * Gauss(), Options.RocofHzPerS);
            }
        }
        return new C37DataFrame(Options.StreamIdCode, C37Time.From(timestamp, Configuration.TimeBase), blocks);
    }

    private double Gauss()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

/// <summary>
/// PMU data source replaying a time series, e.g. bus results of an <c>Electrical.Dynamics</c> simulation
/// (<c>DynamicSimulationResult.Time</c>, <c>BusVoltagePu</c>, <c>BusFrequencyHz</c>). Each PMU reports its bus voltage phasor [V LN RMS];
/// values are interpolated linearly in time. Without angle series the angle is integrated from the frequency deviation,
/// starting at the bus angle of the network (power-flow solution).
/// </summary>
public sealed class PmuTimeSeriesSource : IPmuDataSource
{
    private readonly double[] _t;
    private readonly double[][] _vm, _f, _theta;
    private readonly double[] _vbase;
    private readonly double _fnom;

    /// <param name="network">Network the series belongs to (bus order = series index, base kV, initial angles).</param>
    /// <param name="timeS">Sample times [s], ascending.</param>
    /// <param name="busVoltagePu">Voltage magnitude [pu] indexed [bus position][sample].</param>
    /// <param name="busFrequencyHz">Frequency [Hz] indexed [bus position][sample].</param>
    /// <param name="busAngleDeg">Optional angle [deg] indexed [bus position][sample] (null = integrate frequency).</param>
    /// <param name="startTime">UTC time of sample <c>timeS[0]</c>.</param>
    /// <param name="pmuBuses">Bus ids with a PMU (default every bus).</param>
    public PmuTimeSeriesSource(PowerNetwork network, double[] timeS, double[][] busVoltagePu, double[][] busFrequencyHz,
        double[][]? busAngleDeg, DateTimeOffset startTime, IEnumerable<int>? pmuBuses = null, ushort streamIdCode = 1, short dataRate = 50)
    {
        if (timeS.Length < 2) throw new ArgumentException("At least two samples are required.", nameof(timeS));
        _t = timeS;
        StartTime = startTime;
        _fnom = network.FrequencyHz;
        var buses = (pmuBuses ?? network.Buses.Select(b => b.Id)).ToArray();
        _vm = new double[buses.Length][]; _f = new double[buses.Length][]; _theta = new double[buses.Length][];
        _vbase = new double[buses.Length];
        var pmus = new List<PmuConfiguration>();
        for (int p = 0; p < buses.Length; p++)
        {
            int idx = network.IndexOf(buses[p]);
            var bus = network.Buses[idx];
            _vm[p] = busVoltagePu[idx];
            _f[p] = busFrequencyHz[idx];
            _vbase[p] = bus.BaseKv * 1000 / Math.Sqrt(3);
            if (busAngleDeg is not null) _theta[p] = busAngleDeg[idx].Select(a => a * Math.PI / 180).ToArray();
            else
            {
                var th = new double[timeS.Length];
                th[0] = bus.VaDeg * Math.PI / 180;
                for (int k = 1; k < th.Length; k++)
                    th[k] = th[k - 1] + Math.PI * (_f[p][k] + _f[p][k - 1] - 2 * _fnom) * (timeS[k] - timeS[k - 1]);
                _theta[p] = th;
            }
            pmus.Add(new PmuConfiguration
            {
                StationName = bus.Name is { Length: > 0 } n ? n : $"BUS {bus.Id}", IdCode = (ushort)bus.Id,
                Phasors = [new PhasorChannel($"V_B{bus.Id}", PhasorKind.Voltage)], NominalFrequencyHz = _fnom,
            });
        }
        Configuration = new C37ConfigFrame { IdCode = streamIdCode, Pmus = pmus, DataRate = dataRate };
        HeaderText = $"Electrical.Net time-series PMU replay: {network.Name}, {buses.Length} PMUs";
    }

    public DateTimeOffset StartTime { get; }
    public C37ConfigFrame Configuration { get; }
    public string HeaderText { get; }

    public C37DataFrame CreateFrame(DateTimeOffset timestamp)
    {
        double s = (timestamp - StartTime).TotalSeconds + _t[0];
        int k = Array.BinarySearch(_t, s);
        if (k < 0) k = ~k - 1;
        k = Math.Clamp(k, 0, _t.Length - 2);
        double w = Math.Clamp((s - _t[k]) / (_t[k + 1] - _t[k]), 0, 1);
        var blocks = new PmuDataBlock[_vm.Length];
        for (int p = 0; p < blocks.Length; p++)
        {
            double Lerp(double[] a) => a[k] + w * (a[k + 1] - a[k]);
            double vm = Lerp(_vm[p]), f = Lerp(_f[p]), th = Lerp(_theta[p]);
            double rocof = (_f[p][k + 1] - _f[p][k]) / (_t[k + 1] - _t[k]);
            blocks[p] = new PmuDataBlock(PmuStatus.None, [Complex.FromPolarCoordinates(vm * _vbase[p], th)], f, rocof);
        }
        return new C37DataFrame(Configuration.IdCode, C37Time.From(timestamp, Configuration.TimeBase), blocks);
    }
}
