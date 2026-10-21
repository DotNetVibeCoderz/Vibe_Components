using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Blazor.AI.MediaPipe;

/// <summary>
/// OpenTelemetry sources for vision processing. Subscribe with
/// <c>.WithTracing(t => t.AddSource(VisionTelemetry.Name)).WithMetrics(m => m.AddMeter(VisionTelemetry.Name))</c>.
/// </summary>
public static class VisionTelemetry
{
    public const string Name = "Blazor.AI.MediaPipe";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Frames = Meter.CreateCounter<long>("blazorai.vision.frames", description: "Frames processed");

    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("blazorai.vision.errors", description: "Frames that failed to process");

    public static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("blazorai.vision.latency", "ms", "Server-side processing time per frame");

    public static readonly Counter<long> Events = Meter.CreateCounter<long>("blazorai.vision.events", description: "Vision events raised (alerts, check-ins, falls, violations)");
}
