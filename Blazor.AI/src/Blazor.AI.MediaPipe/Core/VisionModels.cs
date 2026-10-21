using System.Text.Json.Serialization;
using MediaPipeNet.Imaging;

namespace Blazor.AI.MediaPipe;

/// <summary>Where frames come from.</summary>
public enum VisionSourceKind
{
    /// <summary>A still image (sample, upload or URL); processed once, re-processed on demand.</summary>
    Image,

    /// <summary>Webcam or mobile camera through getUserMedia.</summary>
    Camera,

    /// <summary>A video file (upload or URL).</summary>
    Video,

    /// <summary>An HTTP(S) stream the browser can show: MJPEG over HTTP, HLS or an RTSP-to-HTTP bridge.</summary>
    Stream,
}

/// <summary>One frame handed to a vision component. The image is decoded once and disposed after processing.</summary>
public sealed class VisionFrame
{
    public required MPImage Image { get; init; }

    /// <summary>Monotonic timestamp in milliseconds (camera/video time for live sources).</summary>
    public required long TimestampMs { get; init; }

    /// <summary>True for camera, video and stream frames (tracking across frames applies).</summary>
    public required bool IsLive { get; init; }

    /// <summary>The encoded JPEG as received, for snapshots or vision-language models.</summary>
    public required byte[] Jpeg { get; init; }

    public int Width => Image.Width;

    public int Height => Image.Height;

    /// <summary>Sequence number of this frame within the current source.</summary>
    public long Index { get; init; }
}

/// <summary>Processing statistics shown by components.</summary>
public sealed record VisionStats(double Fps, double LatencyMs, long Frames, int Width, int Height);

/// <summary>
/// What the client draws over the picture. Coordinates are normalized (0–1) to the processed frame, so overlays stay
/// aligned at any display size and when the picture is mirrored.
/// </summary>
public sealed class VisionOverlay
{
    public List<OverlayLandmarks> Landmarks { get; init; } = [];
    public List<OverlayBox> Boxes { get; init; } = [];
    public List<OverlayText> Texts { get; init; } = [];
    public List<OverlayPath> Paths { get; init; } = [];
    public OverlayMask? Mask { get; set; }
    public OverlayHeat? Heat { get; set; }

    /// <summary>A short status line drawn in the corner (e.g. "2 hands · Thumb_Up").</summary>
    public string? Status { get; set; }

    public static VisionOverlay Empty => new();

    public VisionOverlay Merge(VisionOverlay? other)
    {
        if (other is null)
        {
            return this;
        }

        Landmarks.AddRange(other.Landmarks);
        Boxes.AddRange(other.Boxes);
        Texts.AddRange(other.Texts);
        Paths.AddRange(other.Paths);
        Mask ??= other.Mask;
        Heat ??= other.Heat;
        Status ??= other.Status;
        return this;
    }
}

/// <summary>A landmark set: points as flat [x0, y0, x1, y1, …] plus optional connections [a0, b0, a1, b1, …].</summary>
public sealed class OverlayLandmarks
{
    public required float[] Points { get; init; }
    public int[]? Connections { get; init; }
    public string Color { get; init; } = "#3cc4b1";
    public string? PointColor { get; init; }
    public float Radius { get; init; } = 2.5f;
    public float LineWidth { get; init; } = 2f;

    /// <summary>Indices to highlight (drawn larger, in <see cref="HighlightColor"/>).</summary>
    public int[]? Highlight { get; init; }

    public string HighlightColor { get; init; } = "#d9964f";
}

/// <summary>A labelled rectangle in normalized coordinates.</summary>
public sealed class OverlayBox
{
    public float X { get; init; }
    public float Y { get; init; }
    public float W { get; init; }
    public float H { get; init; }
    public string? Label { get; init; }
    public string Color { get; init; } = "#d9964f";
    public bool Dashed { get; init; }
    public bool Fill { get; init; }
}

/// <summary>Text placed at a normalized point.</summary>
public sealed class OverlayText
{
    public float X { get; init; }
    public float Y { get; init; }
    public required string Text { get; init; }
    public string Color { get; init; } = "#ffffff";
    public float Size { get; init; } = 14;
    public bool Background { get; init; } = true;
}

/// <summary>A polyline or polygon in normalized coordinates (flat [x0, y0, …]).</summary>
public sealed class OverlayPath
{
    public required float[] Points { get; init; }
    public string Color { get; init; } = "#d9964f";
    public float Width { get; init; } = 3;
    public bool Closed { get; init; }
    public string? Fill { get; init; }
}

/// <summary>
/// A per-pixel mask (0–255, row-major) downsampled for transport. Modes: tint (colour the subject), cutout (hide the
/// background), replace (put <see cref="BackgroundUrl"/> or <see cref="Color"/> behind the subject), blur (blur the background).
/// </summary>
public sealed class OverlayMask
{
    public int Width { get; init; }
    public int Height { get; init; }
    public required string Data { get; init; }
    public string Mode { get; init; } = "tint";
    public string Color { get; init; } = "#3cc4b1";
    public string? BackgroundUrl { get; init; }
    public float Opacity { get; init; } = 0.55f;
    public int BlurPx { get; init; } = 12;
}

/// <summary>A heat grid (row-major values 0–1) stretched over the frame.</summary>
public sealed class OverlayHeat
{
    public int Cols { get; init; }
    public int Rows { get; init; }
    public required float[] Values { get; init; }
    public float Opacity { get; init; } = 0.5f;
}

/// <summary>Options sent to the browser camera module.</summary>
public sealed class CameraStartOptions
{
    public VisionSourceKind Source { get; init; }
    public string? Url { get; init; }
    public string? DeviceId { get; init; }
    public string FacingMode { get; init; } = "user";
    public bool Mirror { get; init; }
    public double Fps { get; init; } = 12;
    public int MaxWidth { get; init; } = 640;
    public double Quality { get; init; } = 0.72;
    public bool Loop { get; init; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VisionOverlay))]
[JsonSerializable(typeof(CameraStartOptions))]
internal sealed partial class VisionJsonContext : JsonSerializerContext;
