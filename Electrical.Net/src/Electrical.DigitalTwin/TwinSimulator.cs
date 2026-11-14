using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.StateEstimation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.DigitalTwin;

/// <summary>A scripted change of the simulated "real" grid from a given step on.</summary>
public sealed record TwinScenarioEvent(int Step, WhatIfAction Action);

/// <summary>A gross measurement error injected into a SCADA tag after the scan of a step (bad-data demo).</summary>
public sealed record TwinBadDataInjection(int Step, string Tag, double Value);

/// <summary>Configuration of a <see cref="TwinSimulator"/> run.</summary>
public sealed record TwinSimulatorOptions
{
    public int Steps { get; init; } = 24;
    public TimeSpan StepSize { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Start time (local clock drives the load shape). Default: Monday 2026-01-05 00:00 WIB.</summary>
    public DateTimeOffset Start { get; init; } = new(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
    /// <summary>Customer class shaping the time-varying load.</summary>
    public LoadProfileKind Profile { get; init; } = LoadProfileKind.Residential;
    /// <summary>Load multiplier at the profile peak (1 = model loads).</summary>
    public double PeakLoadScale { get; init; } = 1.0;
    /// <summary>Relative standard deviation of per-load random variation each step.</summary>
    public double LoadNoise { get; init; } = 0.01;
    public int Seed { get; init; } = 7;
    /// <summary>RTU noise/register layout (null: noise scaled to the network base, σ_P = 0.0005·BaseMva).</summary>
    public RtuOptions? Rtu { get; init; }
    /// <summary>Provide the noise-free profile as forecast to the twin, scaled by (1 + <see cref="ForecastBias"/>).</summary>
    public bool ProvideForecast { get; init; } = true;
    public double ForecastBias { get; init; }
    public IReadOnlyList<TwinScenarioEvent> Events { get; init; } = [];
    public IReadOnlyList<TwinBadDataInjection> BadData { get; init; } = [];
}

/// <summary>Outcome of a simulated run.</summary>
/// <param name="States">Twin states, one per step.</param>
/// <param name="Truth">"Real" power-flow solution of each step.</param>
/// <param name="MaxVoltageErrorPu">max |V̂ − V_true| over all buses and converged steps [pu].</param>
/// <param name="MeanVoltageErrorPu">Mean |V̂ − V_true| [pu].</param>
/// <param name="RaisedAlarms">Alarms raised during the run (TwinAlarmRaised events), in order.</param>
public sealed record TwinSimulationResult(IReadOnlyList<TwinState> States, IReadOnlyList<PowerFlowResult> Truth, double MaxVoltageErrorPu,
    double MeanVoltageErrorPu, IReadOnlyList<ExplainedAlarm> RaisedAlarms, IReadOnlyList<AssetHealth> AssetHealth);

/// <summary>
/// Deterministic closed-loop driver for a <see cref="GridTwin"/>: a "real" grid (clone of the model) follows a daily load profile with
/// seeded noise and scripted events; each step it is solved by Newton–Raphson, published through an <see cref="RtuSimulator"/> as Modbus
/// registers, scanned into a <see cref="TagDatabase"/>, optionally corrupted (bad data), ingested by the twin and estimated.
/// </summary>
public sealed class TwinSimulator
{
    private readonly PowerNetwork _truth;
    private readonly (double P, double Q)[] _baseLoads;
    private readonly Random _rng;
    private readonly List<PowerFlowResult> _truthResults = [];
    private readonly List<ExplainedAlarm> _raised = [];
    private RtuSimulator? _rtu;
    private int _step;

    public TwinSimulator(PowerNetwork network, TwinSimulatorOptions? options = null, GridTwinOptions? twinOptions = null, IGridEventBus? bus = null)
    {
        Options = options ?? new TwinSimulatorOptions();
        _truth = network.Clone();
        _baseLoads = _truth.Loads.Select(l => (l.P, l.Q)).ToArray();
        _rng = new Random(Options.Seed);
        Bus = bus ?? new InMemoryGridEventBus();
        twinOptions ??= new GridTwinOptions { Sigmas = new TagMeasurementSigmas(PowerMw: 0.001 * network.BaseMva) };
        Twin = new GridTwin(network, twinOptions, Bus);
        Tags = new TagDatabase();
        Bus.Subscribe<TwinAlarmRaised>(e => { lock (_raised) _raised.Add(e.Alarm); });
        if (Options.ProvideForecast) Twin.SetForecast(ForecastSeries(Options.Steps));
    }

    public TwinSimulatorOptions Options { get; }
    public GridTwin Twin { get; }
    public TagDatabase Tags { get; }
    public IGridEventBus Bus { get; }
    /// <summary>The simulated real grid (loads of the last step).</summary>
    public PowerNetwork Truth => _truth;
    public RtuSimulator? Rtu => _rtu;
    public int CurrentStep => _step;

    /// <summary>Noise-free load multiplier at a step (profile shape × peak scale).</summary>
    public double LoadScaleAt(int step)
    {
        var t = Options.Start + Options.StepSize * step;
        return Options.PeakLoadScale * SyntheticProfiles.Shape(Options.Profile, t.Hour + t.Minute / 60.0, t.DayOfWeek);
    }

    /// <summary>Forecast of the total system load [MW] for <paramref name="steps"/> steps (noise-free profile × (1 + bias)).</summary>
    public TimeSeries ForecastSeries(int steps)
    {
        double total = _baseLoads.Sum(l => l.P);
        var v = new double[steps];
        for (int i = 0; i < steps; i++) v[i] = total * LoadScaleAt(i) * (1 + Options.ForecastBias);
        return new TimeSeries(Options.Start, Options.StepSize, v, "Load forecast", "MW");
    }

    /// <summary>Advances the real grid one step and runs one twin cycle.</summary>
    public TwinState Step()
    {
        int k = _step++;
        var ts = Options.Start + Options.StepSize * k;
        foreach (var e in Options.Events.Where(e => e.Step == k)) e.Action.Apply(_truth);
        double scale = LoadScaleAt(k);
        for (int i = 0; i < _truth.Loads.Count; i++)
        {
            double f = scale * (1 + Options.LoadNoise * MeasurementGenerator.Gaussian(_rng));
            _truth.Loads[i].P = _baseLoads[i].P * f;
            _truth.Loads[i].Q = _baseLoads[i].Q * f;
        }
        var pf = PowerFlowSolver.Solve(_truth, PowerFlowOptions.Default with { FlatStart = _truthResults.Count == 0 });
        if (pf.Converged) pf.ApplyTo(_truth);
        _truthResults.Add(pf);
        if (_rtu is null)
        {
            var o = Options.Rtu ?? new RtuOptions
            {
                PowerNoiseMw = 0.0005 * _truth.BaseMva, VoltageNoisePu = 0.001, Seed = Options.Seed + 1,
                BranchBaseAddress = (ushort)Math.Max(2000, 6 * _truth.BusCount + 16),
                GeneratorBaseAddress = (ushort)Math.Max(6000, Math.Max(2000, 6 * _truth.BusCount + 16) + 10 * _truth.Branches.Count + 16),
            };
            _rtu = new RtuSimulator(pf, o);
            _rtu.RegisterTags(Tags);
        }
        else _rtu.Publish(pf);
        Tags.Scan(_rtu.Map, _rtu.Options.UnitId, ts);
        foreach (var bad in Options.BadData.Where(b => b.Step == k)) Tags.UpdateValue(bad.Tag, bad.Value, ts);
        Twin.IngestTags(Tags);
        return Twin.Update(ts);
    }

    /// <summary>Runs the remaining steps and summarizes the estimation accuracy.</summary>
    public TwinSimulationResult Run()
    {
        var states = new List<TwinState>();
        while (_step < Options.Steps) states.Add(Step());
        double max = 0, sum = 0; int cnt = 0;
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            var t = _truthResults[^(states.Count - i)];
            if (!s.Converged || !t.Converged) continue;
            for (int b = 0; b < s.Vm.Length; b++)
            {
                double e = Math.Abs(s.Vm[b] - t.Buses[b].Vm);
                max = Math.Max(max, e); sum += e; cnt++;
            }
        }
        List<ExplainedAlarm> raised;
        lock (_raised) raised = [.. _raised];
        return new TwinSimulationResult(states, _truthResults.ToArray(), max, cnt == 0 ? double.NaN : sum / cnt, raised, Twin.AssetHealth);
    }
}

/// <summary>Creates twins wired to the DI event bus and clock.</summary>
public interface IGridTwinFactory
{
    GridTwin Create(PowerNetwork network, GridTwinOptions? options = null);
}

internal sealed class GridTwinFactory(IGridEventBus bus, TimeProvider time) : IGridTwinFactory
{
    public GridTwin Create(PowerNetwork network, GridTwinOptions? options = null) => new(network, options, bus, time);
}

public static class DigitalTwinBuilderExtensions
{
    /// <summary>Registers <see cref="IGridTwinFactory"/> (and the PowerFlow, StateEstimation, SCADA and Forecasting modules).</summary>
    public static ElectricalBuilder AddDigitalTwin(this ElectricalBuilder b)
    {
        b.AddPowerFlow().AddStateEstimation().AddScada().AddForecasting();
        if (b.TryAddModule("DigitalTwin")) b.Services.TryAddSingleton<IGridTwinFactory, GridTwinFactory>();
        return b;
    }
}
