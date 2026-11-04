using System.Numerics;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Gallery;

/// <summary>
/// Detailed models generated with Rodin (Hyper3D) and cleaned up in Blender (Z-up, +Y forward, ~6k triangles,
/// base-colour texture). They are embedded resources so they also work in Blazor WebAssembly.
/// Each model is parsed once; callers get the shared geometry (instancing).
/// </summary>
public static class Assets
{
    private static readonly Lazy<Node?> s_tank = new(() => Load("tank"));
    private static readonly Lazy<Node?> s_jet = new(() => Load("jet"));

    /// <summary>Main battle tank, 7 m long. Falls back to <see cref="Models.Tank"/> if the asset cannot be loaded.</summary>
    public static Node Tank() => s_tank.Value ?? Models.Tank();

    /// <summary>Fighter jet, 15 m long. Falls back to <see cref="Models.Jet"/>.</summary>
    public static Node Jet() => s_jet.Value ?? Models.Jet();

    private static Node? Load(string name)
    {
        try
        {
            var asm = typeof(Assets).Assembly;
            using var obj = asm.GetManifestResourceStream($"Assets.{name}.obj");
            if (obj is null) return null;
            var node = new ObjReaderWriter().ReadNode(obj, new ReadOptions { LoadTextures = false });
            using var png = asm.GetManifestResourceStream($"Assets.{name}_tex0.png");
            var material = new Material { Diffuse = Vector4.One, Ambient = new Vector3(0.3f), Specular = new Vector3(0.25f), Shininess = 24 };
            Texture2D? texture = png is null ? null : new Texture2D(new PngReader().ReadImage(png));
            foreach (var g in FindNodesVisitor.FindAll<Geometry>(node))
            {
                var ss = g.GetOrCreateStateSet();
                ss.Material = material;
                ss.Texture = texture;
            }
            node.Name = name;
            return node;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or FormatException)
        {
            return null;
        }
    }
}
