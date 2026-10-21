using Blazor.AI.Components.Infrastructure;
using Microsoft.AspNetCore.Components;

namespace Blazor.AI.MediaPipe;

/// <summary>
/// Base for analytics views over a <see cref="VisionEventStream"/>: subscribes to <see cref="VisionEventStream.Published"/>
/// (re-subscribing when the parameter changes) and re-renders at most every <see cref="RefreshMs"/> ms.
/// </summary>
public abstract class StreamComponentBase : AiComponentBase
{
    private VisionEventStream? _subscribed;
    private int _pending;

    /// <summary>The stream to show; defaults to the per-user stream registered by AddBlazorAIMediaPipe.</summary>
    [Parameter] public VisionEventStream? Events { get; set; }

    /// <summary>Minimum time between re-renders while events arrive.</summary>
    [Parameter] public int RefreshMs { get; set; } = 400;

    [Inject] private VisionEventStream? ScopedStream { get; set; }

    protected VisionEventStream Stream => Events ?? ScopedStream ?? VisionEventStream.Shared;

    /// <summary>Events currently held by the stream, oldest first (the stream's own "cleared" marker excluded).</summary>
    protected IReadOnlyList<VisionEvent> Current { get; private set; } = [];

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_subscribed, Stream))
        {
            if (_subscribed is not null)
            {
                _subscribed.Published -= OnPublished;
            }

            _subscribed = Stream;
            _subscribed.Published += OnPublished;
        }

        Current = Stream.Snapshot().Where(x => x.Kind != "cleared").ToList();
        OnEvents(Current);
    }

    /// <summary>Called on the renderer with a fresh snapshot (oldest first) before each re-render.</summary>
    protected virtual void OnEvents(IReadOnlyList<VisionEvent> events)
    {
    }

    private void OnPublished(VisionEvent e)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            await Task.Delay(RefreshMs);
            Interlocked.Exchange(ref _pending, 0);
            Current = Stream.Snapshot().Where(x => x.Kind != "cleared").ToList();
            OnEvents(Current);
            StateHasChanged();
        });
    }

    public override ValueTask DisposeAsync()
    {
        if (_subscribed is not null)
        {
            _subscribed.Published -= OnPublished;
        }

        return base.DisposeAsync();
    }
}
