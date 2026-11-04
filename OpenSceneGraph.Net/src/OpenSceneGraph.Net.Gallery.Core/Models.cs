using System.Numerics;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Gallery;

/// <summary>Procedural low-poly models used across the gallery (all Z-up, facing +Y).</summary>
public static class Models
{
    private static Node Part(Geometry g, Vector3 at, Quaternion? rot = null, Vector3? scale = null)
    {
        var pat = new PositionAttitudeTransform { Position = at, Attitude = rot ?? Quaternion.Identity, Scale = scale ?? Vector3.One };
        pat.AddChild(g);
        return pat;
    }

    public static Group Tank(Vector4? camo = null)
    {
        var c = camo ?? Color.FromHex("#5b6b3a");
        var dark = Color.FromHex("#2d3320");
        var g = new Group { Name = "Tank" };
        g.AddChild(Part(Shapes.Box(3.4f, 6.8f, 1.2f, c), new Vector3(0, 0, 0.9f)));
        g.AddChild(Part(Shapes.Box(1.0f, 7.0f, 1.0f, dark), new Vector3(-1.9f, 0, 0.55f)));
        g.AddChild(Part(Shapes.Box(1.0f, 7.0f, 1.0f, dark), new Vector3(1.9f, 0, 0.55f)));
        var turret = new PositionAttitudeTransform { Name = "turret", Position = new Vector3(0, -0.4f, 1.9f) };
        turret.AddChild(Shapes.Box(2.4f, 3.0f, 0.9f, Vector4.Lerp(c, Vector4.One, 0.08f)));
        turret.AddChild(Part(Shapes.Cylinder(0.14f, 4.6f, 12, dark), new Vector3(0, 3.6f, 0.05f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)));
        g.AddChild(turret);
        return g;
    }

    public static Group Jet(Vector4? color = null)
    {
        var c = color ?? Color.FromHex("#8a96a3");
        var g = new Group { Name = "Jet" };
        g.AddChild(Part(Shapes.Capsule(0.7f, 9f, 16, 6, c), Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)));
        g.AddChild(Part(Shapes.Box(9f, 2.4f, 0.18f, c), new Vector3(0, -0.6f, -0.1f)));
        g.AddChild(Part(Shapes.Box(3.6f, 1.2f, 0.14f, c), new Vector3(0, -3.9f, 0.2f)));
        g.AddChild(Part(Shapes.Box(0.14f, 1.4f, 1.8f, c), new Vector3(0, -3.9f, 1.0f)));
        g.AddChild(Part(Shapes.Sphere(0.45f, 12, 8, Color.FromHex("#1b2a3a")), new Vector3(0, 2.4f, 0.45f), scale: new Vector3(1, 2.2f, 0.8f)));
        return g;
    }

    public static Group Quadcopter()
    {
        var body = Color.FromHex("#e5e7eb");
        var arm = Color.FromHex("#374151");
        var g = new Group { Name = "Quadcopter" };
        g.AddChild(Shapes.Box(0.6f, 0.6f, 0.2f, body));
        for (int i = 0; i < 4; i++)
        {
            float a = MathF.PI / 4 + i * MathF.PI / 2;
            var dir = new Vector3(MathF.Cos(a), MathF.Sin(a), 0);
            g.AddChild(Part(Shapes.Box(0.9f, 0.08f, 0.06f, arm), dir * 0.45f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, a)));
            var rotor = new PositionAttitudeTransform { Position = dir * 0.85f + new Vector3(0, 0, 0.1f) };
            rotor.AddChild(Shapes.Cylinder(0.32f, 0.02f, 16, new Vector4(0.2f, 0.2f, 0.25f, 0.55f)));
            rotor.AddUpdateCallback(new Animation.SpinCallback(Vector3.UnitZ, 1800 * (i % 2 == 0 ? 1 : -1)));
            g.AddChild(rotor);
        }
        return g;
    }

    public static Group Ship(Vector4? hull = null)
    {
        var g = new Group { Name = "Ship" };
        var h = hull ?? Color.FromHex("#56606b");
        g.AddChild(Part(Shapes.Box(8, 40, 4, h), new Vector3(0, 0, 0.5f)));
        g.AddChild(Part(Shapes.Cone(4, 0.1f, 8, 4, h), new Vector3(0, 23.5f, 0.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2), new Vector3(1, 1, 0.5f)));
        g.AddChild(Part(Shapes.Box(6, 10, 5, Color.FromHex("#9aa3ad")), new Vector3(0, -6, 5)));
        g.AddChild(Part(Shapes.Box(3, 4, 4, Color.FromHex("#b8c0c8")), new Vector3(0, -6, 9.5f)));
        g.AddChild(Part(Shapes.Cylinder(0.25f, 8, 8, Color.FromHex("#2f3640")), new Vector3(0, -6, 15)));
        g.AddChild(Part(Shapes.Cylinder(1.1f, 2, 12, Color.FromHex("#3d4651")), new Vector3(0, 10, 3.5f)));
        return g;
    }

    public static Group Car(Vector4 color)
    {
        var g = new Group { Name = "Car" };
        g.AddChild(Part(Shapes.Box(1.8f, 4.2f, 0.7f, color), new Vector3(0, 0, 0.6f)));
        g.AddChild(Part(Shapes.Box(1.6f, 2.2f, 0.6f, Vector4.Lerp(color, Color.Black, 0.35f)), new Vector3(0, -0.3f, 1.2f)));
        foreach (var (x, y) in (ReadOnlySpan<(float, float)>)[(-0.9f, 1.3f), (0.9f, 1.3f), (-0.9f, -1.3f), (0.9f, -1.3f)])
            g.AddChild(Part(Shapes.Cylinder(0.35f, 0.3f, 12, Color.FromHex("#1f2328")), new Vector3(x, y, 0.35f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)));
        return g;
    }

    public static Group Tree(float height = 6, Random? rng = null)
    {
        var g = new Group { Name = "Tree" };
        float tint = (float)(rng?.NextDouble() ?? 0.5) * 0.15f;
        g.AddChild(Part(Shapes.Cylinder(0.18f * height / 6, height * 0.35f, 6, Color.FromHex("#5b4030")), new Vector3(0, 0, height * 0.175f)));
        g.AddChild(Part(Shapes.Cone(height * 0.28f, 0, height * 0.75f, 7, new Vector4(0.18f + tint, 0.42f + tint, 0.2f, 1)), new Vector3(0, 0, height * 0.62f)));
        return g;
    }

    public static Group Building(float w, float d, float h, Vector4 color, bool lit = false)
    {
        var g = new Group { Name = "Building" };
        g.AddChild(Part(Shapes.Box(w, d, h, color), new Vector3(0, 0, h / 2)));
        if (lit)
        {
            // emissive window bands
            int floors = System.Math.Max(1, (int)(h / 3.2f));
            var win = Material.Emissive(new Vector3(1f, 0.82f, 0.45f));
            for (int f = 0; f < floors; f++)
            {
                var band = Shapes.Box(w + 0.05f, d + 0.05f, 0.6f);
                band.GetOrCreateStateSet().Material = win;
                g.AddChild(Part(band, new Vector3(0, 0, 1.6f + f * 3.2f)));
            }
        }
        return g;
    }

    public static Group RadarSite()
    {
        var g = new Group { Name = "Radar" };
        g.AddChild(Part(Shapes.Cylinder(2.5f, 3, 24, Color.FromHex("#c9ced6")), new Vector3(0, 0, 1.5f)));
        var dish = new PositionAttitudeTransform { Name = "dish", Position = new Vector3(0, 0, 4.2f) };
        dish.AddChild(Part(Shapes.Box(6, 0.4f, 2.4f, Color.FromHex("#e8ecf1")), Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f)));
        dish.AddUpdateCallback(new Animation.SpinCallback(Vector3.UnitZ, 72));
        g.AddChild(dish);
        return g;
    }

    /// <summary>A flat-shaded transparent cone used for sensor footprints and radar volumes.</summary>
    public static Geometry Beam(float radius, float length, Vector4 color)
    {
        var cone = Shapes.Cone(radius, 0.01f, length, 32, color);
        cone.GetOrCreateStateSet().MakeTransparent();
        cone.StateSet!.CullFace = CullFaceMode.None;
        cone.StateSet.Lighting = false;
        cone.StateSet.CastShadows = false;
        return cone;
    }

    /// <summary>Ring (circle line) in the XY plane.</summary>
    public static Geometry Ring(float radius, Vector4 color, int segments = 96)
    {
        var pts = new List<Vector3>();
        for (int i = 0; i <= segments; i++)
        {
            float a = MathF.Tau * i / segments;
            pts.Add(new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0));
        }
        return Shapes.LineStrip(pts, color);
    }
}
