using System.Numerics;
using AquaCulture.DigitalTwin;
using AquaCulture.Gallery.Shared;
using AquaCulture.Visualization;
using Avalonia.Controls;
using ThreeNet;
using ThreeNet.Avalonia;

namespace AquaCulture.Gallery.Desktop;

/// <summary>
/// 3D digital pond rendered with ThreeNet (Rust/wgpu): dikes, liner, water coloured by DO, paddle wheels and sensor buoys, stock as
/// instanced meshes. Interactive in the window (orbit controller); rendered offscreen for documentation screenshots.
/// </summary>
internal static class Twin3D
{
    private static bool? _available;

    /// <summary>True when the ThreeNet native core loads and a GPU adapter is present.</summary>
    public static bool Available
    {
        get
        {
            if (_available is { } a) return a;
            try { ThreeNetRuntime.Initialize(); _available = true; }
            catch (Exception) { _available = false; }
            return _available.Value;
        }
    }

    private static Vector4 Col(string hex) { var c = Rgba.Parse(hex); return MathHelpers.FromHex((uint)(c.R << 16 | c.G << 8 | c.B)); }

    /// <summary>Builds the scene; returns the scene, camera and paddle-wheel rotor nodes.</summary>
    public static (Scene Scene, Node Camera, List<Node> Rotors) BuildScene(TwinState state, bool aerators, double biomassRatio)
    {
        var scene = new Scene();
        var env = scene.Environment;
        env.Background = Col("#e9f1ee");
        env.AmbientColor = new Vector3(0.95f, 0.98f, 1f);
        env.AmbientIntensity = 0.5f;
        scene.Environment = env;

        float w = 40, l = 50, depth = 1.3f;
        var dikeMat = scene.CreateMaterial(Col("#9c8460"), 0f, 0.95f);
        var grassMat = scene.CreateMaterial(Col("#b9c79a"), 0f, 1f);
        void Box(float sx, float sy, float sz, Vector3 pos, Material m) { var n = scene.AddMesh(scene.CreateBoxGeometry(sx, sy, sz), m); n.Position = pos; }
        // ground as four slabs around the pond, dikes on top
        Box(200, 0.4f, 75, new Vector3(0, 0.4f, -(l / 2 + 3 + 37.5f)), grassMat);
        Box(200, 0.4f, 75, new Vector3(0, 0.4f, l / 2 + 3 + 37.5f), grassMat);
        Box(80, 0.4f, l + 6, new Vector3(-(w / 2 + 3 + 40), 0.4f, 0), grassMat);
        Box(80, 0.4f, l + 6, new Vector3(w / 2 + 3 + 40, 0.4f, 0), grassMat);
        Box(w + 6, 1.8f, 3, new Vector3(0, 0.6f, -(l / 2 + 1.5f)), dikeMat);
        Box(w + 6, 1.8f, 3, new Vector3(0, 0.6f, l / 2 + 1.5f), dikeMat);
        Box(3, 1.8f, l, new Vector3(-(w / 2 + 1.5f), 0.6f, 0), dikeMat);
        Box(3, 1.8f, l, new Vector3(w / 2 + 1.5f, 0.6f, 0), dikeMat);
        Box(w, 0.2f, l, new Vector3(0, -depth - 0.3f, 0), scene.CreateMaterial(Col("#2b3236"), 0f, 0.9f));

        var doColor = ColorRamp.Oxygen.WithRange(0, 10)[state.Water.DissolvedOxygen ?? 6];
        var water = scene.AddMesh(scene.CreatePlaneGeometry(w, l, 8, 8), scene.CreateMaterial(MathHelpers.FromHex((uint)(doColor.R << 16 | doColor.G << 8 | doColor.B)), 0.05f, 0.15f));
        water.EulerAngles = new Vector3(-MathF.PI / 2, 0, 0);

        // paddle wheels
        var rotors = new List<Node>();
        var bladeMat = scene.CreateMaterial(Col("#1f6f8b"), 0.1f, 0.6f);
        var floatMat = scene.CreateMaterial(Col("#e9e3d2"), 0f, 0.8f);
        var motorMat = scene.CreateMaterial(Col("#d9a13b"), 0.2f, 0.5f);
        foreach (var (x, z) in new[] { (-w * 0.28f, -l * 0.22f), (w * 0.28f, l * 0.22f) })
        {
            var root = scene.CreateNode(); root.Position = new Vector3(x, 0.3f, z);
            var rotor = scene.CreateNode(root);
            for (int k = 0; k < 2; k++)
                for (int i = 0; i < 8; i++)
                {
                    float a = i / 8f * MathF.PI * 2;
                    var p = scene.AddMesh(scene.CreateBoxGeometry(0.25f, 1.4f, 0.06f), bladeMat, rotor);
                    p.Position = new Vector3(k == 0 ? -0.9f : 0.9f, MathF.Sin(a) * 0.5f, MathF.Cos(a) * 0.5f);
                    p.EulerAngles = new Vector3(-a, 0, 0);
                }
            rotors.Add(rotor);
            var fl = scene.AddMesh(scene.CreateBoxGeometry(0.5f, 0.5f, 1.6f), floatMat, root); fl.Position = new Vector3(-1.7f, -0.15f, 0);
            var fr = scene.AddMesh(scene.CreateBoxGeometry(0.5f, 0.5f, 1.6f), floatMat, root); fr.Position = new Vector3(1.7f, -0.15f, 0);
            var mo = scene.AddMesh(scene.CreateBoxGeometry(0.5f, 0.45f, 0.45f), motorMat, root); mo.Position = new Vector3(0, 0.35f, 0);
        }
        // buoys
        var buoyMat = scene.CreateMaterial(Col("#ffffff"), 0f, 0.4f);
        var ledMat = scene.CreateMaterial(MaterialOptions.Basic(Col(state.ActiveAlerts.Count > 0 ? "#ff4a3a" : "#5fe0a0")));
        foreach (var (x, z) in new[] { (-12f, -8f), (0f, 10f), (12f, -8f) })
        {
            var b = scene.AddMesh(scene.CreateSphereGeometry(0.45f), buoyMat); b.Position = new Vector3(x, 0.1f, z);
            var led = scene.AddMesh(scene.CreateSphereGeometry(0.14f, 12, 8), ledMat); led.Position = new Vector3(x, 1.4f, z);
        }
        // stock (instanced)
        int n = (int)Math.Clamp(1400 * biomassRatio, 40, 1400);
        var rng = new Core.Data.DeterministicRandom(4);
        var animal = scene.AddMesh(scene.CreateBoxGeometry(0.7f, 0.12f, 0.18f), scene.CreateMaterial(Col("#d8c3a5"), 0f, 0.5f));
        var transforms = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
            transforms[i] = Matrix4x4.CreateRotationY((float)rng.Uniform(0, 6.28)) * Matrix4x4.CreateTranslation((float)rng.Uniform(-w / 2 + 1, w / 2 - 1), (float)rng.Uniform(-0.9, -0.15), (float)rng.Uniform(-l / 2 + 1, l / 2 - 1));
        animal.SetInstances(transforms);

        var sun = scene.AddLight(Light.Directional(Vector3.One, 2.6f));
        sun.Position = new Vector3(40, 60, 25); sun.LookAt(Vector3.Zero);
        var cam = scene.AddCamera(Camera.Perspective(42f.ToRadians(), 0.1f, 500f), new Vector3(38, 34, 52));
        cam.LookAt(new Vector3(0, -0.5f, 0));
        return (scene, cam, rotors);
    }

    /// <summary>Interactive view (orbit with the mouse); paddle wheels spin when aerators run.</summary>
    public static Control? CreateView(TwinState state, bool aerators, double biomassRatio)
    {
        if (!Available) return null;
        try
        {
            var (scene, cam, rotors) = BuildScene(state, aerators, biomassRatio);
            var view = new ThreeNetView { Scene = scene, Camera = cam, Height = 420 };
            var orbit = new OrbitController(view, cam) { Distance = 75, Target = new Vector3(0, -0.5f, 0), Yaw = 0.62f, Pitch = 0.5f };
            view.Frame += (_, e) => { if (aerators) foreach (var r in rotors) r.EulerAngles = new Vector3(-(float)e.TotalSeconds * 4, 0, 0); };
            view.DetachedFromVisualTree += (_, _) => { orbit.Dispose(); };
            return view;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Offscreen render to PNG (documentation screenshots); null when no GPU adapter is available.</summary>
    public static byte[]? RenderPng(TwinState state, bool aerators, double biomassRatio, int width = 900, int height = 470)
    {
        if (!Available) return null;
        try
        {
            var (scene, cam, _) = BuildScene(state, aerators, biomassRatio);
            using (scene)
            {
                using var r = Renderer.CreateOffscreen(new RendererOptions { Width = width, Height = height, MsaaSamples = 4 });
                r.Render(scene, cam);
                var px = r.ReadPixels();
                var rgba = new Rgba[width * height];
                for (int i = 0; i < rgba.Length; i++) rgba[i] = new Rgba(px[i * 4], px[i * 4 + 1], px[i * 4 + 2], 255);
                return PngEncoder.Encode(rgba, width, height);
            }
        }
        catch (Exception) { return null; }
    }
}
