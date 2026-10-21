using System.Text;
using Blazor.AI.Abstractions;
using Blazor.AI.Abstractions.Security;
using Blazor.AI.Components.Infrastructure;
using Blazor.AI.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blazor.AI.MediaPipe;

/// <summary>
/// Base for vision components. Override <see cref="Process"/>: it runs off the circuit thread for each frame with this
/// component's <see cref="Tasks"/> (MediaPipe.NET, VIDEO mode for live frames). Panels refresh at most a few times per
/// second. <see cref="AskAboutFrameAsync"/> sends the latest frame plus computed facts to a vision-capable model and falls
/// back to the facts alone.
/// </summary>
public abstract class VisionComponentBase : AiComponentBase
{
    private VisionTasks? _tasks;
    private DateTime _lastRender;

    [Inject] protected IOptions<MediaPipeVisionOptions> VisionOptions { get; set; } = default!;
    [Inject] protected IServiceProvider Services { get; set; } = default!;

    protected CameraViewer? Camera;

    /// <summary>Height of the camera view (CSS length).</summary>
    [Parameter] public string Height { get; set; } = "420px";

    /// <summary>Sample pictures offered when no camera is used.</summary>
    [Parameter] public IReadOnlyList<VisionSample>? Samples { get; set; }

    /// <summary>Start with the webcam instead of a sample.</summary>
    [Parameter] public bool StartWithCamera { get; set; }

    /// <summary>Requested frames per second for live sources.</summary>
    [Parameter] public double Fps { get; set; } = 12;

    /// <summary>Language for AI answers; null follows the model's default.</summary>
    [Parameter] public string? Language { get; set; }

    protected VisionTasks Tasks => _tasks ??= new VisionTasks(VisionOptions.Value);

    /// <summary>Default sample pictures for this component.</summary>
    protected virtual IReadOnlyList<VisionSample> DefaultSamples => VisionSamples.People;

    protected IReadOnlyList<VisionSample> EffectiveSamples => Samples ?? DefaultSamples;

    protected string? Narration { get; set; }

    /// <summary>The latest processed frame size and whether it was live.</summary>
    protected (int Width, int Height, bool Live) LastFrame { get; private set; }

    /// <summary>Analyses one frame (called off the UI thread). Return the overlay to draw.</summary>
    protected abstract VisionOverlay? Process(VisionFrame frame);

    /// <summary>Wire this to <see cref="CameraViewer.OnFrame"/>.</summary>
    protected async Task<VisionOverlay?> HandleFrameAsync(VisionFrame frame)
    {
        // Inference is CPU-bound; keep the circuit free. One frame at a time per component (the browser waits).
        var overlay = await Task.Run(() => Process(frame));
        LastFrame = (frame.Width, frame.Height, frame.IsLive);
        if (!frame.IsLive || (DateTime.UtcNow - _lastRender).TotalMilliseconds > 250)
        {
            _lastRender = DateTime.UtcNow;
            _ = InvokeAsync(StateHasChanged);
        }

        return overlay;
    }

    /// <summary>Wire this to <see cref="CameraViewer.OnSourceChanged"/>: tracking state is per source.</summary>
    protected virtual void HandleSourceChanged(VisionSourceKind kind) => _tasks?.Reset();

    /// <summary>Records a vision event in the audit trail (when guardrail auditing is enabled) and the telemetry counter.</summary>
    protected void RecordEvent(string summary)
    {
        VisionTelemetry.Events.Add(1);
        Services.GetService<IAuditTrail>()?.Record(new AuditEntry(DateTimeOffset.UtcNow, AuditKind.Info, "mediapipe", null, summary, "vision"));
    }

    /// <summary>
    /// Streams an answer into <see cref="Narration"/> about the latest frame. The frame is attached as an image for
    /// vision-capable models; if the model rejects images, it answers from <paramref name="facts"/> only.
    /// </summary>
    protected Task AskAboutFrameAsync(string role, string question, string facts, bool attachImage = true) => RunAsync(async ct =>
    {
        var system = $"You are {role}. Answer from what is visible and from the measured facts; say when something is uncertain. Be concise." +
                     (Language is null ? string.Empty : $" Answer in {Language}.");
        var text = PromptTags.Wrap(PromptTags.Input, question) + "\n" + PromptTags.Wrap(PromptTags.Context, facts);
        var builder = new StringBuilder();
        Narration = string.Empty;
        var options = AiChatOptions.ForTask(AiTasks.Insight);
        var jpeg = Camera?.LastJpeg;
        var client = ClientFactory.GetChatClient(Provider);
        List<ChatMessage> Messages(bool withImage) =>
        [
            new(ChatRole.System, system),
            new(ChatRole.User, withImage && jpeg is { Length: > 0 } ? [new TextContent(text), new DataContent(jpeg, "image/jpeg")] : [new TextContent(text)]),
        ];

        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(Messages(attachImage), options, ct))
            {
                builder.Append(update.Text);
                Narration = builder.ToString();
                RenderThrottled();
            }
        }
        catch (Exception ex) when (attachImage && builder.Length == 0 && ex is not OperationCanceledException and not AiGuardrailException)
        {
            // Not a vision model (or images disabled): answer from the facts alone.
            await foreach (var update in client.GetStreamingResponseAsync(Messages(false), options, ct))
            {
                builder.Append(update.Text);
                Narration = builder.ToString();
                RenderThrottled();
            }
        }
    });

    public override async ValueTask DisposeAsync()
    {
        _tasks?.Dispose();
        _tasks = null;
        await base.DisposeAsync();
    }
}
