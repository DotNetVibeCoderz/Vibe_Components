using System.Numerics;

namespace OpenSceneGraph.Net.State;

/// <summary>Surface material (<c>osg::Material</c>), physically-inspired but compatible with Blinn-Phong.</summary>
public sealed class Material
{
    public Vector4 Diffuse { get; set; } = new(0.8f, 0.8f, 0.8f, 1f);
    public Vector3 Ambient { get; set; } = new(0.2f);
    public Vector3 Specular { get; set; } = new(0.3f);
    public Vector3 Emission { get; set; } = Vector3.Zero;
    public float Shininess { get; set; } = 32f;

    /// <summary>0 = dielectric, 1 = metal. Used by renderers that support PBR shading; ignored by Blinn-Phong.</summary>
    public float Metallic { get; set; }
    public float Roughness { get; set; } = 0.6f;

    /// <summary>Reflection strength of the environment (0..1), used by renderers supporting reflection.</summary>
    public float Reflectivity { get; set; }

    public bool IsTransparent => Diffuse.W < 0.999f;

    public static Material FromColor(Vector4 c) => new() { Diffuse = c, Ambient = new Vector3(c.X, c.Y, c.Z) * 0.25f };
    public static Material FromColor(float r, float g, float b, float a = 1f) => FromColor(new Vector4(r, g, b, a));
    public static Material FromHex(string hex) => FromColor(Color.FromHex(hex));

    public static Material Emissive(Vector3 color) => new() { Diffuse = new Vector4(color, 1), Emission = color, Specular = Vector3.Zero };
}

public enum CullFaceMode : byte { None, Back, Front }
public enum BlendMode : byte { Opaque, AlphaBlend, Additive, Multiply }
public enum PolygonMode : byte { Fill, Line, Point }
public enum DepthFunction : byte { Less, LessEqual, Always, Greater }

/// <summary>Well-known render bins: lower draws first; transparent bins are depth-sorted back to front.</summary>
public static class RenderBins
{
    public const int Background = -100;
    public const int Opaque = 0;
    public const int Transparent = 10;
    public const int Overlay = 100;
}

/// <summary>
/// Collection of rendering state attached to a node and inherited by its subgraph (<c>osg::StateSet</c>).
/// Unset (null) values inherit from the parent; set values override unless the parent is marked <see cref="Override"/>.
/// </summary>
public sealed class StateSet
{
    private Material? _material;
    private Texture2D? _texture;
    private ShaderProgram? _program;
    private bool? _lighting;
    private CullFaceMode? _cullFace;
    private BlendMode? _blend;
    private PolygonMode? _polygonMode;
    private bool? _depthTest;
    private bool? _depthWrite;
    private int? _renderBin;
    private float? _lineWidth;
    private float? _pointSize;
    private bool? _castShadows;
    private bool? _receiveShadows;
    private bool? _fog;

    public int Version { get; private set; }
    private void Touch() => Version++;

    public Material? Material { get => _material; set { _material = value; Touch(); } }
    public Texture2D? Texture { get => _texture; set { _texture = value; Touch(); } }
    public ShaderProgram? Program { get => _program; set { _program = value; Touch(); } }
    public bool? Lighting { get => _lighting; set { _lighting = value; Touch(); } }
    public CullFaceMode? CullFace { get => _cullFace; set { _cullFace = value; Touch(); } }
    public BlendMode? Blend { get => _blend; set { _blend = value; Touch(); } }
    public PolygonMode? PolygonMode { get => _polygonMode; set { _polygonMode = value; Touch(); } }
    public bool? DepthTest { get => _depthTest; set { _depthTest = value; Touch(); } }
    public bool? DepthWrite { get => _depthWrite; set { _depthWrite = value; Touch(); } }
    public int? RenderBin { get => _renderBin; set { _renderBin = value; Touch(); } }
    public float? LineWidth { get => _lineWidth; set { _lineWidth = value; Touch(); } }
    public float? PointSize { get => _pointSize; set { _pointSize = value; Touch(); } }
    public bool? CastShadows { get => _castShadows; set { _castShadows = value; Touch(); } }
    public bool? ReceiveShadows { get => _receiveShadows; set { _receiveShadows = value; Touch(); } }
    public bool? Fog { get => _fog; set { _fog = value; Touch(); } }

    /// <summary>When true, attributes set here win over the same attributes set by descendants (<c>osg::StateAttribute::OVERRIDE</c>).</summary>
    public bool Override { get; set; }

    /// <summary>Uniform values for custom shader programs.</summary>
    public UniformCollection Uniforms { get; } = new();

    /// <summary>Configures the state for alpha-blended, depth-sorted rendering.</summary>
    public StateSet MakeTransparent(BlendMode mode = BlendMode.AlphaBlend)
    {
        Blend = mode;
        RenderBin = RenderBins.Transparent;
        DepthWrite = false;
        return this;
    }
}

/// <summary>Named uniform values (<c>osg::Uniform</c>). Supported types: float, int, bool, Vector2/3/4, Matrix4x4.</summary>
public sealed class UniformCollection
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    public int Version { get; private set; }
    public int Count => _values.Count;

    public void Set(string name, float v) => SetObj(name, v);
    public void Set(string name, int v) => SetObj(name, v);
    public void Set(string name, bool v) => SetObj(name, v);
    public void Set(string name, Vector2 v) => SetObj(name, v);
    public void Set(string name, Vector3 v) => SetObj(name, v);
    public void Set(string name, Vector4 v) => SetObj(name, v);
    public void Set(string name, Matrix4x4 v) => SetObj(name, v);

    private void SetObj(string name, object v) { _values[name] = v; Version++; }

    public bool TryGet(string name, out object? value) => _values.TryGetValue(name, out value);
    public IEnumerable<KeyValuePair<string, object>> All => _values;
}

/// <summary>
/// GLSL program (<c>osg::Program</c>). Sources are written against the OpenSceneGraph.Net shader interface
/// (see docs: shaders.md); the renderer injects the version header and built-in uniforms.
/// </summary>
public sealed class ShaderProgram
{
    public required string VertexSource { get; init; }
    public required string FragmentSource { get; init; }
    public string? Name { get; init; }
    public long Id { get; } = Interlocked.Increment(ref s_id);
    private static long s_id;
}

public enum LightType : byte { Directional, Point, Spot }

/// <summary>A light (<c>osg::Light</c>). Position it with a <see cref="Scene.LightSource"/> node or add it to the viewer.</summary>
public sealed class Light
{
    public LightType Type { get; set; } = LightType.Directional;
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1f;

    /// <summary>Local position (point/spot) in the light source's coordinate frame.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Direction the light travels (directional/spot), in the light source's coordinate frame.</summary>
    public Vector3 Direction { get; set; } = Vector3.Normalize(new Vector3(-0.4f, 0.3f, -1f));

    public float Range { get; set; } = 100f;
    public float SpotCutoffDegrees { get; set; } = 30f;
    public bool CastShadows { get; set; }

    public static Light Sun(Vector3 direction, float intensity = 1f, bool shadows = true) =>
        new() { Type = LightType.Directional, Direction = Vector3.Normalize(direction), Intensity = intensity, CastShadows = shadows };

    public static Light Point(Vector3 position, Vector3 color, float range, float intensity = 1f) =>
        new() { Type = LightType.Point, Position = position, Color = color, Range = range, Intensity = intensity };
}

/// <summary>Color helpers (sRGB hex to linear-ish RGBA used across the library).</summary>
public static class Color
{
    public static Vector4 FromHex(string hex)
    {
        var s = hex.AsSpan().TrimStart('#');
        static float B(ReadOnlySpan<char> c) => int.Parse(c, System.Globalization.NumberStyles.HexNumber) / 255f;
        return s.Length switch
        {
            6 => new Vector4(B(s[..2]), B(s[2..4]), B(s[4..6]), 1f),
            8 => new Vector4(B(s[..2]), B(s[2..4]), B(s[4..6]), B(s[6..8])),
            _ => throw new FormatException($"Invalid color '{hex}'."),
        };
    }

    public static Vector4 Rgb(float r, float g, float b, float a = 1f) => new(r, g, b, a);

    public static Vector4 FromHsv(float h, float s, float v, float a = 1f)
    {
        h = ((h % 360) + 360) % 360 / 60f;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return new Vector4(r, g, b, a);
    }

    public static readonly Vector4 White = new(1, 1, 1, 1);
    public static readonly Vector4 Black = new(0, 0, 0, 1);
    public static readonly Vector4 Red = new(0.9f, 0.15f, 0.12f, 1);
    public static readonly Vector4 Green = new(0.2f, 0.75f, 0.3f, 1);
    public static readonly Vector4 Blue = new(0.15f, 0.4f, 0.95f, 1);
    public static readonly Vector4 Yellow = new(1f, 0.85f, 0.1f, 1);
    public static readonly Vector4 Orange = new(1f, 0.5f, 0.1f, 1);
    public static readonly Vector4 Gray = new(0.5f, 0.5f, 0.5f, 1);
}
