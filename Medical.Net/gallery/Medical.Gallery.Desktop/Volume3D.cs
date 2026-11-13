using System.Numerics;
using Avalonia.Controls;
using Medical.Core;
using Medical.Gallery.Shared;
using ThreeNet;
using ThreeNet.Avalonia;
using ThreeNet.Interop;

namespace Medical.Gallery.Desktop;

/// <summary>
/// The skull and haemorrhage isosurfaces rendered with ThreeNet (Rust / wgpu): interactive with an orbit controller in the
/// window, offscreen to PNG for documentation screenshots.
/// </summary>
internal static class Volume3D
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

    private static Vector4 Hex(string hex)
    {
        uint v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return MathHelpers.FromHex(v);
    }

    private static Geometry Upload(Scene scene, DisplayMesh m)
    {
        int n = m.Positions.Length / 3;
        var vertices = new Vertex[n];
        for (int i = 0; i < n; i++)
            vertices[i] = new Vertex(new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]),
                                     new Vector3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]), Vector2.Zero);
        var indices = new uint[m.Indices.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = (uint)m.Indices[i];
        return scene.CreateGeometry(vertices, indices, PrimitiveTopology.TriangleList);
    }

    public static (Scene Scene, Node Camera) BuildScene(Volume3DView v)
    {
        var scene = new Scene();
        var env = scene.Environment;
        env.Background = Hex("#eef3f4");
        env.AmbientColor = new Vector3(0.95f, 0.97f, 1f);
        env.AmbientIntensity = 0.55f;
        scene.Environment = env;

        var lesionMat = scene.CreateMaterial(Hex(v.Lesion.Color), 0.05f, 0.5f);
        scene.AddMesh(Upload(scene, v.Lesion), lesionMat);

        var bone = MaterialOptions.Pbr(Hex(v.Skull.Color) with { W = 0.5f }, 0f, 0.6f);
        bone.AlphaMode = AlphaMode.Blend;
        bone.DepthWrite = false;
        bone.CullMode = CullMode.Back;
        bone.RenderOrder = 2;
        scene.AddMesh(Upload(scene, v.Skull), scene.CreateMaterial(bone));

        var key = scene.AddLight(Light.Directional(Vector3.One, 2.4f));
        key.Position = new Vector3(v.Radius * 1.5f, v.Radius * 2f, v.Radius * 2.5f); key.LookAt(Vector3.Zero);
        var rim = scene.AddLight(Light.Directional(new Vector3(1f, 0.95f, 0.9f), 1.0f));
        rim.Position = new Vector3(-v.Radius * 2f, v.Radius, -v.Radius * 2f); rim.LookAt(Vector3.Zero);
        var cam = scene.AddCamera(Camera.Perspective(35f.ToRadians(), v.Radius / 50, v.Radius * 20), new Vector3(v.Radius * 1.05f, v.Radius * 0.55f, v.Radius * 1.65f));
        cam.LookAt(Vector3.Zero);
        return (scene, cam);
    }

    /// <summary>Interactive view (drag to orbit, wheel to zoom); null without a GPU.</summary>
    public static Control? CreateView(Volume3DView v, double height = 460)
    {
        if (!Available) return null;
        try
        {
            var (scene, cam) = BuildScene(v);
            var view = new ThreeNetView { Scene = scene, Camera = cam, Height = height };
            var orbit = new OrbitController(view, cam) { Distance = v.Radius * 2.0f, Target = Vector3.Zero, Yaw = 0.55f, Pitch = 0.3f };
            view.DetachedFromVisualTree += (_, _) => orbit.Dispose();
            return view;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Offscreen render to PNG (documentation screenshots); null when no GPU adapter is available.</summary>
    public static byte[]? RenderPng(Volume3DView v, int width = 860, int height = 460)
    {
        if (!Available) return null;
        try
        {
            var (scene, cam) = BuildScene(v);
            using (scene)
            {
                using var r = Renderer.CreateOffscreen(new RendererOptions { Width = width, Height = height, MsaaSamples = 4 });
                r.Render(scene, cam);
                var px = r.ReadPixels();
                var rgba = new byte[width * height * 4];
                for (int i = 0; i < width * height; i++) { rgba[i * 4] = px[i * 4]; rgba[i * 4 + 1] = px[i * 4 + 1]; rgba[i * 4 + 2] = px[i * 4 + 2]; rgba[i * 4 + 3] = 255; }
                return PngEncoder.EncodeRgba(rgba, width, height);
            }
        }
        catch (Exception) { return null; }
    }
}
