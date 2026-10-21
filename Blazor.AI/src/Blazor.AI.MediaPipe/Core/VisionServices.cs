using MediaPipeNet;
using MediaPipeNet.Tasks.Vision;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blazor.AI.MediaPipe;

/// <summary>
/// Turns a detected face into an identity vector. The default combines face-mesh geometry with a generic image
/// embedding of the face crop: good for demos and small, cooperative groups, not biometric-grade. Register your own
/// (for example an ArcFace ONNX model) to replace it.
/// </summary>
public interface IFaceEmbedder
{
    float[] Embed(VisionFrame frame, FaceLandmarks face, VisionTasks tasks);
}

/// <summary>
/// Default <see cref="IFaceEmbedder"/>: a MobileNet image embedding of the face crop, aligned so the eyes are level.
/// Measured on the sample photos: the same face under brightness, crop and 8° rotation changes scores 0.94–0.99;
/// different people score 0.4–0.65, so a threshold around 0.8 separates them. Demo-grade, not biometric-grade.
/// </summary>
public sealed class MeshFaceEmbedder : IFaceEmbedder
{
    public float[] Embed(VisionFrame frame, FaceLandmarks face, VisionTasks tasks)
    {
        var l = face.Landmarks;
        var (x, y, w, h) = Overlay.Bounds(l, 0.04f);

        // Roll from the eye line (pixel space) so tilted heads crop the same way.
        var dx = (l[263].X - l[33].X) * frame.Width;
        var dy = (l[263].Y - l[33].Y) * frame.Height;
        var roll = (float)Math.Atan2(dy, dx);

        // Square crop in pixels, expressed back in normalized units.
        var side = Math.Max(w * frame.Width, h * frame.Height) * 1.05f;
        var rect = new NormalizedRect(x + w / 2, y + h / 2, side / frame.Width, side / frame.Height, roll);
        return Normalize(tasks.Embed(frame.Image, rect).Values.ToArray());
    }

    private static float[] Normalize(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 1e-9)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] = (float)(v[i] / n);
            }
        }

        return v;
    }
}

/// <summary>A person enrolled in the <see cref="FaceGallery"/>.</summary>
public sealed class EnrolledFace(string name)
{
    public string Name { get; } = name;
    public List<float[]> Samples { get; } = [];
    public string? Thumbnail { get; set; }
    public DateTimeOffset EnrolledAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-user (scoped) gallery of enrolled faces used by attendance and verification components.</summary>
public sealed class FaceGallery
{
    private readonly List<EnrolledFace> _people = [];

    public IReadOnlyList<EnrolledFace> People => _people;

    public event Action? Changed;

    public void Enroll(string name, float[] embedding, string? thumbnail = null)
    {
        var p = _people.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null)
        {
            p = new EnrolledFace(name.Trim());
            _people.Add(p);
        }

        p.Samples.Add(embedding);
        p.Thumbnail ??= thumbnail;
        Changed?.Invoke();
    }

    public void Remove(string name)
    {
        _people.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Changed?.Invoke();
    }

    /// <summary>Best match by cosine similarity (max over each person's samples).</summary>
    public (EnrolledFace? Person, double Similarity) Match(float[] embedding)
    {
        EnrolledFace? best = null;
        var bestScore = -1.0;
        foreach (var p in _people)
        {
            foreach (var s in p.Samples)
            {
                var c = Cosine(embedding, s);
                if (c > bestScore)
                {
                    bestScore = c;
                    best = p;
                }
            }
        }

        return (best, bestScore);
    }

    public static double Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na < 1e-12 || nb < 1e-12 ? 0 : dot / Math.Sqrt(na * nb);
    }
}

internal sealed class VisionHubOptionsSetup(IOptions<MediaPipeVisionOptions> vision) : IConfigureOptions<HubOptions>
{
    public void Configure(HubOptions options)
    {
        if (options.MaximumReceiveMessageSize is null || options.MaximumReceiveMessageSize < vision.Value.MaxFrameMessageBytes)
        {
            options.MaximumReceiveMessageSize = vision.Value.MaxFrameMessageBytes;
        }
    }
}

/// <summary>Downloads the common models at startup when <see cref="MediaPipeVisionOptions.PreloadModels"/> is set.</summary>
internal sealed class VisionModelPreloader(IOptions<MediaPipeVisionOptions> options, VisionModelManager models, ILogger<VisionModelPreloader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PreloadModels)
        {
            return;
        }

        try
        {
            await models.EnsureAsync(VisionModelManager.CommonModels, null, stoppingToken);
            logger.LogInformation("MediaPipe.NET models ready.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not pre-download MediaPipe.NET models; they will be fetched on first use.");
        }
    }
}
