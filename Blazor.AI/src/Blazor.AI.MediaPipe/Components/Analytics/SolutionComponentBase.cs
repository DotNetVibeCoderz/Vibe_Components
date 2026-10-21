using Microsoft.AspNetCore.Components;

namespace Blazor.AI.MediaPipe;

/// <summary>
/// Base for solution components (healthcare, agritech, industry…): adds an event stream that dashboards can read and a
/// local event log. Events are also written to the audit trail and the vision telemetry counter.
/// </summary>
public abstract class SolutionComponentBase : VisionComponentBase
{
    private IReadOnlyList<VisionEvent> _log = [];

    /// <summary>Where events go; defaults to the per-user stream registered by AddBlazorAIMediaPipe.</summary>
    [Parameter] public VisionEventStream? Events { get; set; }

    /// <summary>Raised for every event this component publishes.</summary>
    [Parameter] public EventCallback<VisionEvent> OnEvent { get; set; }

    /// <summary>Name used as the event source (defaults to the component type).</summary>
    [Parameter] public string? SourceName { get; set; }

    [Inject] private VisionEventStream? ScopedStream { get; set; }

    protected VisionEventStream Stream => Events ?? ScopedStream ?? VisionEventStream.Shared;

    /// <summary>The camera's live statistics (fps, round-trip latency), for <see cref="RealtimeMetrics"/>.</summary>
    public VisionStats? CameraStats => Camera?.Stats;

    /// <summary>This component's latest events, newest first.</summary>
    protected IReadOnlyList<VisionEvent> Log => _log;

    /// <summary>Publishes an event (safe to call from <see cref="VisionComponentBase.Process"/>); optionally attaches a snapshot.</summary>
    protected void Publish(string kind, string message, VisionEventSeverity severity = VisionEventSeverity.Info, bool snapshot = false,
        IReadOnlyDictionary<string, int>? counts = null, (float X, float Y)? position = null)
    {
        var e = new VisionEvent(DateTimeOffset.UtcNow, SourceName ?? GetType().Name, kind, message, severity) { Counts = counts, Position = position };
        _ = InvokeAsync(async () =>
        {
            if (snapshot && Camera is not null)
            {
                e = e with { Snapshot = await Camera.SnapshotAsync() };
            }

            _log = [e, .. _log.Take(49)];
            Stream.Publish(e);
            if (severity != VisionEventSeverity.Info)
            {
                RecordEvent($"{e.Source}: {kind}: {message}");
            }

            await OnEvent.InvokeAsync(e);
            StateHasChanged();
        });
    }

    /// <summary>Renders the local event log as compact rows.</summary>
    protected RenderFragment EventLog(int max = 8) => builder =>
    {
        var seq = 0;
        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "class", "baiv-log");
        builder.AddAttribute(seq++, "aria-live", "polite");
        if (_log.Count == 0)
        {
            builder.OpenElement(seq++, "span");
            builder.AddAttribute(seq++, "class", "bai-small bai-muted");
            builder.AddContent(seq++, "No events yet.");
            builder.CloseElement();
        }

        foreach (var e in _log.Take(max))
        {
            builder.OpenElement(seq++, "div");
            builder.OpenElement(seq++, "span");
            builder.AddAttribute(seq++, "class", e.Severity switch { VisionEventSeverity.Alert => "bai-badge bai-badge-danger", VisionEventSeverity.Warning => "bai-badge bai-badge-ai", _ => "bai-badge" });
            builder.AddContent(seq++, e.At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            builder.CloseElement();
            builder.AddContent(seq++, " " + e.Message);
            builder.CloseElement();
        }

        builder.CloseElement();
    };
}
