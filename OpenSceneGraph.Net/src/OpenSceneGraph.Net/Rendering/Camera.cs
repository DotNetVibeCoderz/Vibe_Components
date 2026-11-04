using System.Numerics;
using OpenSceneGraph.Net.Math;

namespace OpenSceneGraph.Net.Rendering;

public enum ProjectionMode { Perspective, Orthographic }

public enum StereoMode { None, AnaglyphRedCyan, SideBySide, TopBottom }

/// <summary>Viewport in pixels, origin at the top-left (UI convention).</summary>
public readonly record struct Viewport(int X, int Y, int Width, int Height)
{
    public float Aspect => Height > 0 ? (float)Width / Height : 1f;
}

/// <summary>The main view camera (<c>osg::Camera</c> as used by <c>osgViewer::View</c>).</summary>
public sealed class Camera
{
    public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.CreateLookAt(new Vector3(0, -10, 5), Vector3.Zero, Vector3.UnitZ);
    public Viewport Viewport { get; set; } = new(0, 0, 1280, 720);

    public ProjectionMode ProjectionMode { get; set; } = ProjectionMode.Perspective;
    public float FieldOfViewY { get; set; } = 45f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 10000f;

    /// <summary>Half-height in world units for orthographic projection.</summary>
    public float OrthoHalfHeight { get; set; } = 10f;

    /// <summary>Like OSG's COMPUTE_NEAR_FAR_USING_BOUNDING_VOLUMES: tightens near/far to the visible bounds each frame.</summary>
    public bool AutoNearFar { get; set; } = true;
    public float NearFarRatio { get; set; } = 0.0005f;

    public StereoMode Stereo { get; set; } = StereoMode.None;
    public float EyeSeparation { get; set; } = 0.065f;

    public Vector4 ClearColor { get; set; } = new(0.06f, 0.08f, 0.11f, 1f);

    public Matrix4x4 ProjectionMatrix => BuildProjection(NearPlane, FarPlane);

    public Matrix4x4 BuildProjection(float near, float far)
    {
        float aspect = Viewport.Aspect;
        return ProjectionMode == ProjectionMode.Perspective
            ? MathUtil.Perspective(FieldOfViewY, aspect, near, far)
            : Matrix4x4.CreateOrthographic(OrthoHalfHeight * 2 * aspect, OrthoHalfHeight * 2, near, far);
    }

    public Vector3 Eye => Matrix4x4.Invert(ViewMatrix, out var inv) ? inv.Translation : Vector3.Zero;

    public void LookAt(Vector3 eye, Vector3 center, Vector3? up = null) =>
        ViewMatrix = Matrix4x4.CreateLookAt(eye, center, up ?? Vector3.UnitZ);
}

public readonly record struct FogSettings(bool Enabled, Vector3 Color, float Start, float End)
{
    public static FogSettings Disabled => new(false, Vector3.One, 100, 1000);
}

public sealed class ShadowSettings
{
    public bool Enabled { get; set; }
    public int MapSize { get; set; } = 2048;

    /// <summary>World-space radius around the view focus covered by the shadow map.</summary>
    public float Radius { get; set; } = 60f;
    public float Bias { get; set; } = 0.0015f;
    public float Softness { get; set; } = 1.5f;
}

/// <summary>Post-processing effects (applied by renderers that support them, see <c>RendererCapabilities</c>).</summary>
public sealed class PostEffects
{
    public bool Hdr { get; set; }
    public float Exposure { get; set; } = 1f;
    public bool Bloom { get; set; }
    public float BloomThreshold { get; set; } = 0.9f;
    public float BloomIntensity { get; set; } = 0.6f;
    public bool Vignette { get; set; }
}

/// <summary>Scene-wide environment: background, ambient light, fog, shadows and post effects.</summary>
public sealed class SceneEnvironment
{
    /// <summary>Optional vertical gradient (top colour); bottom is the camera clear colour.</summary>
    public Vector4? SkyTopColor { get; set; } = new(0.13f, 0.18f, 0.26f, 1);
    public Vector3 AmbientLight { get; set; } = new(0.25f, 0.27f, 0.3f);

    /// <summary>Adds a light that follows the camera (osgViewer's default HEADLIGHT).</summary>
    public bool HeadLight { get; set; } = true;
    public float HeadLightIntensity { get; set; } = 0.55f;

    public FogSettings Fog { get; set; } = FogSettings.Disabled;
    public ShadowSettings Shadows { get; } = new();
    public PostEffects Post { get; } = new();

    /// <summary>Environment colour used for reflections on materials with <c>Reflectivity &gt; 0</c>.</summary>
    public Vector3 ReflectionColor { get; set; } = new(0.55f, 0.65f, 0.8f);
}
