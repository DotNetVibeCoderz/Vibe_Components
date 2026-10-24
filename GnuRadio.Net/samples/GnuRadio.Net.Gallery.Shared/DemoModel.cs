using GnuRadio.Sinks;

namespace GnuRadio.Gallery;

/// <summary>Gallery language.</summary>
public enum Language
{
    En,
    Id,
}

/// <summary>English / Bahasa Indonesia text pair.</summary>
public sealed record Text(string En, string Id)
{
    public string this[Language language] => language == Language.Id ? Id : En;

    public static Text Same(string both) => new(both, both);
}

/// <summary>Demo categories, mirroring the GNU Radio feature areas.</summary>
public enum DemoCategory
{
    Spectrum,
    Analog,
    Digital,
    Synchronization,
    Channels,
    ErrorCorrection,
    Filters,
    Extensions,
    Interop,
}

public static class Categories
{
    public static Text Name(DemoCategory c) => c switch
    {
        DemoCategory.Spectrum => new("Spectrum & waterfall", "Spektrum & waterfall"),
        DemoCategory.Analog => new("Analog modulation", "Modulasi analog"),
        DemoCategory.Digital => new("Digital modulation", "Modulasi digital"),
        DemoCategory.Synchronization => new("Synchronization", "Sinkronisasi"),
        DemoCategory.Channels => new("Channel models", "Model kanal"),
        DemoCategory.ErrorCorrection => new("Error correction", "Koreksi galat"),
        DemoCategory.Filters => new("Filters & DSP", "Filter & DSP"),
        DemoCategory.Extensions => new("Extensions", "Ekstensi"),
        DemoCategory.Interop => new("Interop & networking", "Interop & jaringan"),
        _ => Text.Same(c.ToString()),
    };
}

/// <summary>A control shown on a demo's front panel.</summary>
public abstract class DemoParameter(string key, Text label)
{
    public string Key { get; } = key;
    public Text Label { get; } = label;
    public Text? Help { get; init; }
}

/// <summary>A numeric control (knob), or a frequency readout when <see cref="IsFrequency"/>.</summary>
public sealed class NumberParameter(string key, Text label, double min, double max, double step, double value, Action<double> apply)
    : DemoParameter(key, label)
{
    public double Min { get; } = min;
    public double Max { get; } = max;
    public double Step { get; } = step;
    public double Value { get; private set; } = value;
    public string Unit { get; init; } = "";
    public Func<double, string>? Format { get; init; }
    public bool IsFrequency { get; init; }

    public void Set(double value)
    {
        Value = Math.Clamp(value, Min, Max);
        apply(Value);
    }
}

/// <summary>A choice between named options (segmented buttons).</summary>
public sealed class ChoiceParameter(string key, Text label, IReadOnlyList<(string Value, Text Label)> options, string value, Action<string> apply)
    : DemoParameter(key, label)
{
    public IReadOnlyList<(string Value, Text Label)> Options { get; } = options;
    public string Value { get; private set; } = value;

    public void Set(string value)
    {
        Value = value;
        apply(value);
    }
}

/// <summary>A push button that triggers an action (for example "Send packet").</summary>
public sealed class ActionParameter(string key, Text label, Action invoke) : DemoParameter(key, label)
{
    public void Invoke() => invoke();
}

/// <summary>Panel width in the instrument grid.</summary>
public enum PanelSize
{
    Full,
    Half,
    Third,
}

/// <summary>Something a demo displays. The galleries map each kind to their components.</summary>
public abstract record DemoView(Text Title, PanelSize Size = PanelSize.Full, int Height = 240);

public sealed record SpectrumPanel(Text Title, FrequencySink Sink, float MinDb = -100, float MaxDb = 0,
    Func<IReadOnlyList<(double Frequency, string Label)>>? Markers = null, Action<double>? OnTune = null, bool MaxHold = false,
    PanelSize Size = PanelSize.Full, int Height = 240) : DemoView(Title, Size, Height);

public sealed record WaterfallPanel(Text Title, WaterfallSink Sink, float MinDb = -100, float MaxDb = -20,
    Func<IReadOnlyList<(double Frequency, string Label)>>? Markers = null, Action<double>? OnTune = null,
    PanelSize Size = PanelSize.Full, int Height = 240) : DemoView(Title, Size, Height);

public sealed record ConstellationPanel(Text Title, ConstellationSink Sink, IReadOnlyList<Complex32>? Reference = null, float Scale = 1.5f,
    Func<string>? Meta = null, PanelSize Size = PanelSize.Third, int Height = 280) : DemoView(Title, Size, Height);

/// <summary>Oscilloscope; <paramref name="Sink"/> is a <see cref="TimeSink{T}"/> of float or Complex32.</summary>
public sealed record ScopePanel(Text Title, Block Sink, float Min = -1.2f, float Max = 1.2f, int[]? Traces = null,
    PanelSize Size = PanelSize.Half, int Height = 220) : DemoView(Title, Size, Height);

public sealed record VectorPanel(Text Title, VectorFrameSink? Sink = null, Func<float[]?>? Data = null, float Min = -100, float Max = 0,
    double XStart = 0, double XEnd = 1, string XUnit = "", string YUnit = "", Func<IReadOnlyList<(double X, string Label)>>? Markers = null,
    Func<string>? Meta = null, PanelSize Size = PanelSize.Half, int Height = 220) : DemoView(Title, Size, Height);

/// <summary>A line of a <see cref="ChartPanel"/>.</summary>
public sealed record ChartLine(string Name, double[] X, double[] Y, bool Dashed = false, bool Points = false);

public sealed record ChartPanel(Text Title, Func<IReadOnlyList<ChartLine>> Series, Func<int> Revision, double XMin, double XMax, double YMin, double YMax,
    bool LogY = false, string XUnit = "", string YUnit = "", Func<string>? Meta = null, PanelSize Size = PanelSize.Full, int Height = 300)
    : DemoView(Title, Size, Height);

public sealed record FlowgraphPanel(Text Title, Func<Flowgraph?> Flowgraph, PanelSize Size = PanelSize.Full, int Height = 0) : DemoView(Title, Size, Height);

/// <summary>Monospace text that updates (decoded messages, logs, generated code).</summary>
public sealed record TextPanel(Text Title, Func<string> Content, PanelSize Size = PanelSize.Half, int Height = 160) : DemoView(Title, Size, Height);

/// <summary>A live value in the readout strip.</summary>
public sealed record Readout(Text Label, Func<string> Value);

/// <summary>
/// A gallery use case: a flowgraph, its front-panel controls, the displays it feeds, and the sample code that
/// builds it. Both galleries (Blazor and Avalonia) render demos generically from this description.
/// </summary>
public abstract class Demo : IDisposable
{
    private readonly List<DemoView> _views = [];
    private readonly List<Readout> _readouts = [];
    private readonly Lock _lock = new();

    public abstract string Id { get; }
    public abstract DemoCategory Category { get; }
    public abstract Text Title { get; }
    public abstract Text Summary { get; }

    /// <summary>A longer explanation shown under the panels.</summary>
    public virtual Text? Notes => null;

    /// <summary>Self-contained C# showing how to build this demo's signal chain.</summary>
    public abstract string Code { get; }

    public IReadOnlyList<DemoParameter> Parameters => _parameters;
    private readonly List<DemoParameter> _parameters = [];

    public IReadOnlyList<DemoView> Views
    {
        get
        {
            lock (_lock) return [.. _views];
        }
    }

    public IReadOnlyList<Readout> Readouts
    {
        get
        {
            lock (_lock) return [.. _readouts];
        }
    }

    public Flowgraph? Flowgraph { get; private set; }
    public bool IsRunning => Flowgraph?.IsRunning == true;

    /// <summary>Raised when views change (after a restart); UIs re-render.</summary>
    public event Action? Changed;

    /// <summary>Last error from the flowgraph, if any.</summary>
    public string? Error { get; private set; }

    protected void AddParameter(DemoParameter p) => _parameters.Add(p);

    protected void AddView(DemoView v)
    {
        lock (_lock) _views.Add(v);
    }

    protected void AddReadout(Text label, Func<string> value)
    {
        lock (_lock) _readouts.Add(new Readout(label, value));
    }

    /// <summary>Builds the flowgraph and registers views and readouts.</summary>
    protected abstract Flowgraph Build();

    public void Start()
    {
        lock (_lock)
        {
            _views.Clear();
            _readouts.Clear();
        }
        Error = null;
        var fg = Build();
        fg.Error += (b, e) => Error = $"{b.Alias}: {e.Message}";
        Flowgraph = fg;
        fg.Start();
        OnStarted();
        Changed?.Invoke();
    }

    protected virtual void OnStarted()
    {
    }

    public void Stop()
    {
        OnStopping();
        Flowgraph?.Stop();
    }

    protected virtual void OnStopping()
    {
    }

    /// <summary>Stops and rebuilds (for parameters that change the structure).</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
