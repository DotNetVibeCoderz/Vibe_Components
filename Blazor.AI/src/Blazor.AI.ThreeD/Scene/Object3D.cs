using System.Text.Json.Serialization;

namespace Blazor.AI.ThreeD;

/// <summary>
/// One object in a <see cref="Scene3D"/>, described declaratively. The scene diffs objects by <see cref="Id"/>:
/// changing a transform, colour or label updates in place; changing geometry rebuilds just that object.
/// Coordinates are metres, Y up; rotations are radians.
/// </summary>
public sealed record Object3D
{
    public required string Id { get; init; }

    /// <summary>box, sphere, cylinder, cone, torus, icosahedron, capsule, plane, ring, line, tube, points, arrow, surface, text, group or model.</summary>
    public required string Kind { get; init; }

    public string? Parent { get; init; }
    public double[]? Position { get; init; }
    public double[]? Rotation { get; init; }

    /// <summary>A number (uniform) or a 3-element array.</summary>
    public object? Scale { get; init; }

    public string? Color { get; init; }
    public string? Emissive { get; init; }
    public double? EmissiveIntensity { get; init; }
    public double? Opacity { get; init; }
    public double? Metalness { get; init; }
    public double? Roughness { get; init; }
    public bool? Wireframe { get; init; }
    public bool? FlatShading { get; init; }
    public bool? Unlit { get; init; }
    public bool? DoubleSided { get; init; }
    public bool? CastShadow { get; init; }
    public bool? ReceiveShadow { get; init; }
    public bool? Visible { get; init; }

    /// <summary>Edge outline colour for boxes and other primitives.</summary>
    public string? Edges { get; init; }

    // Geometry
    public double[]? Size { get; init; }
    public double? Radius { get; init; }
    public double? RadiusTop { get; init; }
    public double? RadiusBottom { get; init; }
    public double? InnerRadius { get; init; }
    public double? Height { get; init; }
    public double? Tube { get; init; }
    public int? Segments { get; init; }
    public int? Detail { get; init; }
    public IReadOnlyList<double[]>? Points { get; init; }

    /// <summary>Per-vertex or per-point colours (line, points, surface).</summary>
    public IReadOnlyList<string>? Colors { get; init; }

    public double? PointSize { get; init; }
    public bool? Closed { get; init; }
    public bool? Dashed { get; init; }
    public double[]? From { get; init; }
    public double[]? To { get; init; }

    /// <summary>Surface heights, row-major, <see cref="Rows"/> × <see cref="Cols"/>.</summary>
    public IReadOnlyList<double>? Values { get; init; }

    public int? Rows { get; init; }
    public int? Cols { get; init; }

    // Model
    public string? Url { get; init; }
    public string? Clip { get; init; }
    public bool? ClipLoop { get; init; }
    public double? ClipSpeed { get; init; }

    /// <summary>Scale the model so its height is this many metres (origin moved to its base).</summary>
    public double? FitHeight { get; init; }

    /// <summary>Scale the model so its largest dimension is this many metres.</summary>
    public double? FitSize { get; init; }

    public bool? KeepModelColors { get; init; }

    // Interaction and annotation
    public string? Label { get; init; }
    public string? LabelClass { get; init; }
    public double? LabelOffset { get; init; }
    public string? Tooltip { get; init; }
    public bool? Pickable { get; init; }
    public Animate3D? Animate { get; init; }

    // ---- factories

    public static Object3D Box(string id, double[] position, double[] size, string color) => new() { Id = id, Kind = "box", Position = position, Size = size, Color = color };

    public static Object3D Sphere(string id, double[] position, double radius, string color) => new() { Id = id, Kind = "sphere", Position = position, Radius = radius, Color = color };

    public static Object3D Cylinder(string id, double[] position, double radius, double height, string color) => new() { Id = id, Kind = "cylinder", Position = position, Radius = radius, Height = height, Color = color };

    public static Object3D Line(string id, IReadOnlyList<double[]> points, string color, bool dashed = false) => new() { Id = id, Kind = "line", Points = points, Color = color, Dashed = dashed ? true : null, Pickable = false };

    public static Object3D TubePath(string id, IReadOnlyList<double[]> points, double radius, string color) => new() { Id = id, Kind = "tube", Points = points, Radius = radius, Color = color };

    public static Object3D Text(string id, double[] position, string text, string? cssClass = null) => new() { Id = id, Kind = "text", Position = position, Label = text, LabelClass = cssClass, LabelOffset = 0, Pickable = false };

    public static Object3D Model(string id, string url, double[]? position = null, double? fitHeight = null, string? clip = null) => new() { Id = id, Kind = "model", Url = url, Position = position, FitHeight = fitHeight, Clip = clip };
}

/// <summary>Built-in procedural animation applied every frame on the client.</summary>
public sealed record Animate3D
{
    /// <summary>spin, bob, pulse, orbit, path or wander.</summary>
    public required string Type { get; init; }

    public double? Speed { get; init; }
    public double? Amplitude { get; init; }
    public double? Radius { get; init; }
    public string? Axis { get; init; }
    public double[]? Center { get; init; }
    public double? Tilt { get; init; }
    public double? Phase { get; init; }
    public double? SelfSpin { get; init; }
    public IReadOnlyList<double[]>? Points { get; init; }
    public bool? Loop { get; init; }
    public bool? Face { get; init; }
    public double? YawOffset { get; init; }

    public static Animate3D Spin(double speed = 0.6, string axis = "y") => new() { Type = "spin", Speed = speed, Axis = axis };

    public static Animate3D Bob(double amplitude = 0.1, double speed = 1) => new() { Type = "bob", Amplitude = amplitude, Speed = speed };

    public static Animate3D Pulse(double amplitude = 0.08, double speed = 1) => new() { Type = "pulse", Amplitude = amplitude, Speed = speed };

    public static Animate3D Orbit(double radius, double speed, double phase = 0, double tilt = 0, double[]? center = null) => new() { Type = "orbit", Radius = radius, Speed = speed, Phase = phase, Tilt = tilt, Center = center };

    public static Animate3D Path(IReadOnlyList<double[]> points, double speed = 1, bool loop = true, double yawOffset = 0) => new() { Type = "path", Points = points, Speed = speed, Loop = loop, YawOffset = yawOffset };

    public static Animate3D Wander(double radius, double speed = 0.5, double phase = 0, double yawOffset = 0) => new() { Type = "wander", Radius = radius, Speed = speed, Phase = phase, YawOffset = yawOffset };
}

/// <summary>A partial update for an existing object (smoothly tweened when a duration is given).</summary>
public sealed record ObjectPatch
{
    public required string Id { get; init; }
    public double[]? Position { get; init; }
    public double[]? Rotation { get; init; }
    public object? Scale { get; init; }
    public string? Color { get; init; }
    public string? Emissive { get; init; }
    public double? EmissiveIntensity { get; init; }
    public double? Opacity { get; init; }
    public bool? Visible { get; init; }
    public string? Label { get; init; }
    public string? Clip { get; init; }
    public bool? ClipLoop { get; init; }
    public double? ClipSpeed { get; init; }
}

/// <summary>Scene-wide settings: background, camera, controls, lighting and helpers.</summary>
public sealed record Scene3DOptions
{
    /// <summary>Background colour; null keeps the canvas transparent so the page theme shows through.</summary>
    public string? Background { get; init; }

    public CameraOptions Camera { get; init; } = new();
    public ControlsOptions Controls { get; init; } = new();

    /// <summary>studio, day, night or soft.</summary>
    public string Lights { get; init; } = "studio";

    public bool Shadows { get; init; } = true;
    public bool Environment { get; init; } = true;
    public double Exposure { get; init; } = 1.0;
    public GridOptions? Grid { get; init; }
    public GroundOptions? Ground { get; init; }
    public FogOptions? Fog { get; init; }
    public bool PreserveDrawingBuffer { get; init; }
    public string? AriaLabel { get; init; }

    public sealed record CameraOptions
    {
        public double[] Position { get; init; } = [6, 5, 8];
        public double[] Target { get; init; } = [0, 0, 0];
        public double Fov { get; init; } = 45;
    }

    public sealed record ControlsOptions
    {
        public bool Enabled { get; init; } = true;
        public bool AutoRotate { get; init; }
        public double AutoRotateSpeed { get; init; } = 0.8;
        public double? MinDistance { get; init; }
        public double? MaxDistance { get; init; }
        public double? MaxPolarAngle { get; init; }
    }

    public sealed record GridOptions(double Size = 20, int Divisions = 20, string Color = "#5b6bd6", double Opacity = 0.25);

    public sealed record GroundOptions(double Size = 30, string Color = "#10143a");

    public sealed record FogOptions(string? Color = null, double Near = 10, double Far = 60);
}

/// <summary>Raised when the user clicks an object.</summary>
public sealed record PickEvent(string Id, double[] Point);

/// <summary>Raised when a glTF model finishes loading.</summary>
public sealed record ModelInfo(string Id, IReadOnlyList<string> Clips, IReadOnlyList<string> Morphs, int Triangles);

/// <summary>Renderer statistics.</summary>
public sealed record SceneStats(int Triangles, int Calls, int Geometries, int Textures, int Objects);

/// <summary>Result of a mesh simplification.</summary>
public sealed record SimplifyResult(int Before, int After);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Object3D[]))]
[JsonSerializable(typeof(ObjectPatch[]))]
[JsonSerializable(typeof(Scene3DOptions))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(double[]))]
internal sealed partial class ThreeDJsonContext : JsonSerializerContext;
