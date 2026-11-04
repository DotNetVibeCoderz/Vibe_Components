using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Gallery;

public enum SampleCategory
{
    Military,
    Academic,
    Science,
    Medical,
    Office,
    Entertainment,
    Simulator,
    Gis,
    Fundamentals,
}

/// <summary>Bilingual text (English / Bahasa Indonesia).</summary>
public readonly record struct Text(string En, string Id)
{
    public string Get(string lang) => lang.StartsWith("id", StringComparison.OrdinalIgnoreCase) ? Id : En;
}

/// <summary>Live values a sample publishes for the gallery's info panel (speed, altitude, counts...).</summary>
public sealed class SampleTelemetry
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public void Set(string key, string value) { lock (_lock) _values[key] = value; }

    public IReadOnlyList<KeyValuePair<string, string>> Snapshot()
    {
        lock (_lock) return _values.ToList();
    }

    public void Clear() { lock (_lock) _values.Clear(); }
}

/// <summary>What a sample receives when it builds its scene.</summary>
public sealed class SampleContext(Viewer viewer)
{
    public Viewer Viewer { get; } = viewer;
    public SampleTelemetry Telemetry { get; } = new();

    /// <summary>Key bindings the gallery shows to the user (e.g. "W/S" → "throttle").</summary>
    public List<(string Keys, Text Action)> Controls { get; } = [];
}

/// <summary>A gallery entry: metadata, the scene factory and the code shown to the user.</summary>
public sealed class GallerySample
{
    public required string Id { get; init; }
    public required Text Title { get; init; }
    public required Text Description { get; init; }
    public required SampleCategory Category { get; init; }
    public string[] Tags { get; init; } = [];

    /// <summary>Accent colour (hex) used for the card.</summary>
    public string Accent { get; init; } = "#4cc9f0";

    /// <summary>Builds the scene and configures the viewer (manipulator, environment, handlers).</summary>
    public required Func<SampleContext, Node> Build { get; init; }

    /// <summary>Short, copy-pasteable C# showing the essence of the sample.</summary>
    public required string Code { get; init; }

    /// <summary>OSG features demonstrated (shown as badges).</summary>
    public string[] Features { get; init; } = [];
}

/// <summary>Event handler built from a delegate (used by interactive samples).</summary>
public sealed class DelegateEventHandler(Func<InputEvent, Viewer, bool> handler) : IEventHandler
{
    public bool Handle(in InputEvent e, Viewer viewer) => handler(e, viewer);
}

/// <summary>Tracks held keys for samples that drive vehicles.</summary>
public sealed class KeyState : IEventHandler
{
    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);
    public bool this[string key] => _down.Contains(key);
    public bool Any(params string[] keys) => keys.Any(_down.Contains);

    public bool Handle(in InputEvent e, Viewer viewer)
    {
        if (e.Key is null) return false;
        if (e.Type == InputEventType.KeyDown) _down.Add(e.Key);
        else if (e.Type == InputEventType.KeyUp) _down.Remove(e.Key);
        return false; // never consume: the camera may also want keys
    }
}
