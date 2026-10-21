using MediaPipeNet;
using MediaPipeNet.Imaging;
using MediaPipeNet.Inference;
using MediaPipeNet.Inference.Models;
using MediaPipeNet.Tasks;
using MediaPipeNet.Tasks.Vision;
using Microsoft.Extensions.Options;

namespace Blazor.AI.MediaPipe;

/// <summary>Global settings for the MediaPipe.NET runtime.</summary>
public sealed class MediaPipeVisionOptions
{
    /// <summary>ONNX Runtime execution provider: Auto (default), Cpu, Cuda, DirectML or CoreML.</summary>
    public ExecutionProvider Provider { get; set; } = ExecutionProvider.Auto;

    /// <summary>Folder with pre-downloaded models (for offline and edge deployments). Null uses the MediaPipeNet cache.</summary>
    public string? ModelDirectory { get; set; }

    /// <summary>Download missing models from NuGet on first use (default true). Turn off for air-gapped sites.</summary>
    public bool AllowModelDownload { get; set; } = true;

    /// <summary>Download the common models at startup instead of on first use.</summary>
    public bool PreloadModels { get; set; }

    /// <summary>Largest frame message (bytes) the Blazor Server hub accepts; frames are JPEG and usually 15–60 KB.</summary>
    public long MaxFrameMessageBytes { get; set; } = 2 * 1024 * 1024;

    internal BaseOptions ToBaseOptions() => new()
    {
        ModelDirectory = ModelDirectory,
        ModelStore = ModelDirectory is null && AllowModelDownload ? ModelStore.Default : ModelStore.CreateDefault(ModelDirectory, null, AllowModelDownload),
        Inference = new InferenceOptions { Provider = Provider, FallbackToCpu = true },
    };
}

/// <summary>
/// The vision tasks one component uses. Tasks are created on first use and kept for the component's lifetime: live
/// frames go through VIDEO mode (with tracking and smoothing), still images through IMAGE mode. Not thread-safe; a
/// component processes one frame at a time.
/// </summary>
public sealed class VisionTasks(MediaPipeVisionOptions options) : IDisposable
{
    private readonly Dictionary<string, IDisposable> _tasks = [];
    private long _lastTimestamp = -1;

    private T Get<T>(string key, Func<T> create)
        where T : IDisposable
    {
        if (!_tasks.TryGetValue(key, out var task))
        {
            task = create();
            _tasks[key] = task;
        }

        return (T)task;
    }

    private BaseOptions Base => options.ToBaseOptions();

    private static RunningMode Mode(VisionFrame f) => f.IsLive ? RunningMode.Video : RunningMode.Image;

    private long Ts(VisionFrame f)
    {
        // VIDEO mode requires strictly increasing timestamps.
        _lastTimestamp = Math.Max(_lastTimestamp + 1, f.TimestampMs);
        return _lastTimestamp;
    }

    public FaceDetectionResult DetectFaces(VisionFrame f, FaceDetectorModel model = FaceDetectorModel.ShortRange, float minConfidence = 0.5f)
    {
        var t = Get($"facedet:{model}:{minConfidence}:{f.IsLive}", () => FaceDetector.Create(new FaceDetectorOptions { BaseOptions = Base, RunningMode = Mode(f), Model = model, MinDetectionConfidence = minConfidence }));
        return f.IsLive ? t.DetectForVideo(f.Image, Ts(f)) : t.Detect(f.Image);
    }

    public FaceLandmarkResult FaceLandmarks(VisionFrame f, int numFaces = 1, bool blendshapes = true, bool matrices = false)
    {
        var t = Get($"facelm:{numFaces}:{blendshapes}:{matrices}:{f.IsLive}", () => FaceLandmarker.Create(new FaceLandmarkerOptions
        {
            BaseOptions = Base, RunningMode = Mode(f), NumFaces = numFaces, OutputFaceBlendshapes = blendshapes,
            OutputFacialTransformationMatrixes = matrices, SmoothLandmarks = f.IsLive,
        }));
        return f.IsLive ? t.DetectForVideo(f.Image, Ts(f)) : t.Detect(f.Image);
    }

    public HandLandmarkResult Hands(VisionFrame f, int numHands = 2)
    {
        var t = Get($"hands:{numHands}:{f.IsLive}", () => HandLandmarker.Create(new HandLandmarkerOptions { BaseOptions = Base, RunningMode = Mode(f), NumHands = numHands }));
        return f.IsLive ? t.DetectForVideo(f.Image, Ts(f)) : t.Detect(f.Image);
    }

    public GestureRecognitionResult Gestures(VisionFrame f, int numHands = 2, float minScore = 0.5f)
    {
        var t = Get($"gest:{numHands}:{minScore}:{f.IsLive}", () => GestureRecognizer.Create(new GestureRecognizerOptions
        {
            BaseOptions = Base, RunningMode = Mode(f), MinGestureScore = minScore,
            Hands = new HandLandmarkerOptions { BaseOptions = Base, RunningMode = Mode(f), NumHands = numHands },
        }));
        return f.IsLive ? t.RecognizeForVideo(f.Image, Ts(f)) : t.Recognize(f.Image);
    }

    public PoseLandmarkResult Pose(VisionFrame f, PoseModel model = PoseModel.Lite, int numPoses = 1, bool mask = false)
    {
        var t = Get($"pose:{model}:{numPoses}:{mask}:{f.IsLive}", () => PoseLandmarker.Create(new PoseLandmarkerOptions
        {
            BaseOptions = Base, RunningMode = Mode(f), Model = model, NumPoses = numPoses, OutputSegmentationMasks = mask, SmoothLandmarks = f.IsLive,
        }));
        return f.IsLive ? t.DetectForVideo(f.Image, Ts(f)) : t.Detect(f.Image);
    }

    public HolisticResult Holistic(VisionFrame f, bool blendshapes = false, bool mask = false)
    {
        var t = Get($"holistic:{blendshapes}:{mask}:{f.IsLive}", () => HolisticLandmarker.Create(new HolisticLandmarkerOptions
        {
            BaseOptions = Base, RunningMode = Mode(f), OutputFaceBlendshapes = blendshapes, OutputSegmentationMask = mask,
        }));
        return f.IsLive ? t.DetectForVideo(f.Image, Ts(f)) : t.Detect(f.Image);
    }

    public SegmentationResult Segment(VisionFrame f, SegmenterModel model = SegmenterModel.Selfie, bool categoryMask = false)
    {
        var t = Get($"seg:{model}:{categoryMask}:{f.IsLive}", () => ImageSegmenter.Create(new ImageSegmenterOptions
        {
            BaseOptions = Base, RunningMode = Mode(f), Model = model, OutputConfidenceMasks = true, OutputCategoryMask = categoryMask,
            TemporalSmoothing = f.IsLive ? 0.4f : 0f,
        }));
        return f.IsLive ? t.SegmentForVideo(f.Image, Ts(f)) : t.Segment(f.Image);
    }

    /// <summary>Segments the object under a normalized point (MagicTouch); image mode only.</summary>
    public SegmentationResult SegmentAt(VisionFrame f, float x, float y)
    {
        var t = Get("interactive", () => InteractiveSegmenter.Create(new InteractiveSegmenterOptions { BaseOptions = Base, OutputConfidenceMasks = true }));
        return t.Segment(f.Image, RegionOfInterest.FromKeypoint(x, y));
    }

    public ObjectDetectionResult Objects(VisionFrame f, float scoreThreshold = 0.4f, int maxResults = 25, IReadOnlySet<string>? allow = null)
    {
        var key = $"obj:{scoreThreshold}:{maxResults}:{(allow is null ? "" : string.Join(',', allow.Order()))}:{f.IsLive}";
        var t = Get(key, () => ObjectDetector.Create(new ObjectDetectorOptions
        {
            BaseOptions = Base, RunningMode = Mode(f), ScoreThreshold = scoreThreshold, MaxResults = maxResults, CategoryAllowlist = allow!,
        }));
        return f.IsLive ? t.DetectForVideo(f.Image, Ts(f)) : t.Detect(f.Image);
    }

    public ClassificationResult Classify(VisionFrame f, int maxResults = 5, IReadOnlySet<string>? allow = null, NormalizedRect? region = null)
    {
        // Classification is stateless; IMAGE mode keeps it simple for crops and regions.
        var t = Get($"cls:{maxResults}:{(allow is null ? "" : string.Join(',', allow.Order()))}", () => ImageClassifier.Create(new ImageClassifierOptions
        {
            BaseOptions = Base, MaxResults = maxResults, CategoryAllowlist = allow!,
        }));
        return t.Classify(f.Image, region is null ? null : new ImageProcessingOptions(region));
    }

    public Embedding Embed(MPImage image, NormalizedRect? region = null)
    {
        var t = Get("embed", () => ImageEmbedder.Create(new ImageEmbedderOptions { BaseOptions = Base, L2Normalize = true }));
        return t.Embed(image, region is null ? null : new ImageProcessingOptions(region)).Embedding;
    }

    public FaceStylizerResult Stylize(VisionFrame f)
    {
        var t = Get("stylize", () => FaceStylizer.Create(new FaceStylizerOptions { BaseOptions = Base }));
        return t.Stylize(f.Image);
    }

    /// <summary>Forgets tracking state (call when the source changes).</summary>
    public void Reset()
    {
        _lastTimestamp = -1;
        foreach (var task in _tasks.Values)
        {
            switch (task)
            {
                case FaceLandmarker x: x.ResetTracking(); break;
                case HandLandmarker x: x.ResetTracking(); break;
                case GestureRecognizer x: x.ResetTracking(); break;
                case PoseLandmarker x: x.ResetTracking(); break;
                case HolisticLandmarker x: x.ResetTracking(); break;
                case ImageSegmenter x: x.ResetTracking(); break;
            }
        }
    }

    public void Dispose()
    {
        foreach (var task in _tasks.Values)
        {
            task.Dispose();
        }

        _tasks.Clear();
    }
}

/// <summary>Model catalogue and pre-download for offline or edge deployments.</summary>
public sealed class VisionModelManager(IOptions<MediaPipeVisionOptions> options)
{
    /// <summary>Models used by the components, smallest first.</summary>
    public static IReadOnlyList<ModelDescriptor> CommonModels =>
    [
        ModelCatalog.FaceDetectionShortRange, ModelCatalog.FaceLandmarksDetector, ModelCatalog.FaceBlendshapes,
        ModelCatalog.PalmDetection, ModelCatalog.HandLandmarksDetector, ModelCatalog.GestureEmbedder, ModelCatalog.CannedGestureClassifier,
        ModelCatalog.PoseDetection, ModelCatalog.PoseLandmarksLite, ModelCatalog.SelfieSegmenter, ModelCatalog.EfficientDetLite0,
        ModelCatalog.EfficientNetLite0,
    ];

    /// <summary>Every model the catalogue knows.</summary>
    public static IReadOnlyList<ModelDescriptor> AllModels =>
    [
        .. CommonModels, ModelCatalog.FaceDetectionFullRange, ModelCatalog.HandRoiRefinement, ModelCatalog.PoseLandmarksFull, ModelCatalog.SelfieMulticlass,
        ModelCatalog.HairSegmenter, ModelCatalog.DeepLabV3, ModelCatalog.MagicTouch, ModelCatalog.FaceLandmarksDetector192,
    ];

    public ModelStore Store => options.Value.ModelDirectory is null && options.Value.AllowModelDownload
        ? ModelStore.Default
        : ModelStore.CreateDefault(options.Value.ModelDirectory, null, options.Value.AllowModelDownload);

    /// <summary>Path of a model if it is already on disk, otherwise null.</summary>
    public string? FindLocal(ModelDescriptor model) => Store.FindLocal(model);

    public Task EnsureAsync(IEnumerable<ModelDescriptor> models, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Store.EnsureModelsAsync(models, progress!, cancellationToken);

    public IReadOnlyList<ExecutionProvider> AvailableProviders() => ExecutionProviderSelector.GetAvailableProviders();
}
