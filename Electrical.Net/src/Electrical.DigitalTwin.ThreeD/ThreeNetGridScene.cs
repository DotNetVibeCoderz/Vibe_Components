using System.Numerics;
using Electrical.Visualization;
using ThreeNet;
using ThreeNet.Interop;

namespace Electrical.DigitalTwin.ThreeD;

/// <summary>
/// Realizes a <see cref="GridScene3D"/> in a ThreeNet <see cref="Scene"/> (one geometry, material and node per object), keeps the
/// materials in sync with the model's live colours / alarm glow (<see cref="Sync"/>), shows labels as HUD text projected from their
/// world anchors (<see cref="UpdateLabels"/>), and renders off-screen to PNG (<see cref="RenderPng"/>). For Avalonia, assign
/// <see cref="Scene"/> and <see cref="Camera"/> to a <c>ThreeNet.Avalonia.ThreeNetView</c> and call <see cref="Sync"/> /
/// <see cref="UpdateLabels"/> from its <c>Frame</c> event. Requires the ThreeNet native core (wgpu) and a GPU adapter.
/// </summary>
public sealed class ThreeNetGridScene : IDisposable
{
    private readonly Dictionary<SceneObject, (Node Node, Material Material, int Version)> _items = [];
    private readonly Dictionary<SceneObject, OverlayElement> _labels = [];
    private readonly bool _ownsScene;
    private float _fov = 50f * MathF.PI / 180f;

    private ThreeNetGridScene(GridScene3D model, Scene scene, bool ownsScene)
    {
        Model = model; Scene = scene; _ownsScene = ownsScene;
    }

    public GridScene3D Model { get; }
    public Scene Scene { get; }
    public Node Camera { get; private set; } = null!;
    /// <summary>ThreeNet node of every scene object.</summary>
    public Node NodeOf(SceneObject o) => _items[o].Node;
    public Material MaterialOf(SceneObject o) => _items[o].Material;
    public int NodeCount => _items.Count;

    /// <summary>Builds the ThreeNet scene (in <paramref name="scene"/>, or a new one owned by the returned object).</summary>
    public static ThreeNetGridScene Create(GridScene3D model, Scene? scene = null)
    {
        var s = scene ?? new Scene();
        var r = new ThreeNetGridScene(model, s, scene is null);
        var root = s.CreateNode(name: model.Network.Name);
        foreach (var o in model.Objects)
        {
            if (o.Mesh.VertexCount == 0) continue;
            var m = o.Mesh;
            var verts = new Vertex[m.VertexCount];
            for (int i = 0; i < verts.Length; i++) verts[i] = new Vertex(m.Positions[i], m.Normals[i], Vector2.Zero);
            var geo = s.CreateGeometry(verts, m.Indices.ToArray());
            var mat = s.CreateMaterial(Options(o));
            var node = s.AddMesh(geo, mat, root, o.Name);
            r._items[o] = (node, mat, o.Version);
        }
        var env = s.Environment;
        env.Background = new Vector4(0.62f, 0.74f, 0.86f, 1);
        env.AmbientColor = new Vector3(1, 1, 1);
        env.AmbientIntensity = 0.35f;
        s.Environment = env;
        var sun = s.AddLight(Light.Directional(new Vector3(1, 0.97f, 0.92f), 3f));
        sun.Position = new Vector3(200, 400, 150);
        sun.LookAt(Vector3.Zero);
        r.Camera = s.AddCamera(ThreeNet.Camera.Perspective(r._fov, 0.5f, 20000f), Vector3.Zero);
        r.FrameCamera();
        return r;
    }

    /// <summary>Places the camera on the model's bounding sphere direction (<paramref name="zoom"/> = 1 fits the sphere) at <paramref name="elevationDeg"/>.</summary>
    public void FrameCamera(float elevationDeg = 35, float azimuthDeg = 30, float zoom = 0.8f)
    {
        var (lo, hi) = Model.Bounds();
        var c = (lo + hi) / 2;
        float radius = Math.Max(10, (hi - lo).Length() / 2);
        float dist = radius / MathF.Tan(_fov / 2) * zoom;
        float el = elevationDeg * MathF.PI / 180, az = azimuthDeg * MathF.PI / 180;
        Camera.Position = c + dist * new Vector3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
        Camera.LookAt(c);
    }

    /// <summary>Pushes changed colours / emission of the model to the ThreeNet materials. Returns the number of materials updated.</summary>
    public int Sync()
    {
        int n = 0;
        foreach (var (o, item) in _items.ToArray())
        {
            if (item.Version == o.Version) continue;
            item.Material.Options = Options(o);
            _items[o] = item with { Version = o.Version };
            if (_labels.TryGetValue(o, out var lbl) && o.Label is not null) lbl.Text = o.Label;
            n++;
        }
        return n;
    }

    private static MaterialOptions Options(SceneObject o)
    {
        var c = GlbExporter.ToLinear(o.Color);
        var mo = MaterialOptions.Pbr(new Vector4(c, 1), o.Metallic, o.Roughness);
        if (o.Emission > 0)
        {
            mo.Emissive = GlbExporter.ToLinear(GridScene3D.AlarmColor(o.AlarmSeverity));
            mo.EmissiveIntensity = 2 * o.Emission;
        }
        return mo;
    }

    /// <summary>
    /// Creates (on first call) and positions HUD text labels for objects with a label, projecting their anchors with the current camera
    /// into a viewport of <paramref name="width"/> × <paramref name="height"/> pixels. Labels behind the camera are hidden.
    /// </summary>
    public void UpdateLabels(int width, int height, float fontSize = 13)
    {
        if (!Model.Options.Labels && !Model.Options.BranchLabels) return;
        var view = Matrix4x4.CreateLookAt(Camera.WorldPosition, Camera.WorldPosition + Forward(Camera), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(_fov, (float)width / Math.Max(1, height), 0.5f, 20000f);
        var vp = view * proj;
        foreach (var o in Model.Objects)
        {
            if (o.Label is null) continue;
            if (!_labels.TryGetValue(o, out var el))
            {
                el = Scene.Overlay.AddText(o.Label, Vector2.Zero, fontSize, new Vector4(1, 1, 1, 1));
                _labels[o] = el;
            }
            var clip = Vector4.Transform(new Vector4(o.LabelAnchor, 1), vp);
            bool visible = clip.W > 0;
            el.Visible = visible;
            if (!visible) continue;
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * width, sy = (1 - (clip.Y / clip.W * 0.5f + 0.5f)) * height;
            el.Offset = new Vector2(sx, sy);
        }
    }

    private static Vector3 Forward(Node camera)
    {
        var m = camera.WorldMatrix;
        return Vector3.Normalize(-new Vector3(m.M31, m.M32, m.M33)); // cameras face −Z in local space
    }

    /// <summary>Renders one frame off-screen and returns it as PNG bytes (throws <see cref="ThreeNetException"/> without a GPU adapter).</summary>
    public byte[] RenderPng(int width = 1280, int height = 720, bool labels = true)
    {
        using var renderer = Renderer.CreateOffscreen(new RendererOptions { Width = width, Height = height, VSync = false, MsaaSamples = 4 });
        Sync();
        if (labels) UpdateLabels(width, height);
        renderer.Render(Scene, Camera);
        var rgba = renderer.ReadPixels();
        return PngEncoder.Encode(rgba, width, height);
    }

    public void Dispose()
    {
        if (_ownsScene) Scene.Dispose();
    }
}
