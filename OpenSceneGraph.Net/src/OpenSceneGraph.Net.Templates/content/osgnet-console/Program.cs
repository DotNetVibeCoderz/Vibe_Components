// OpenSceneGraph.Net console app - Created by Gravicode Studios, led by Kang Fadhil.
using System.Numerics;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Native;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;
using OpenSceneGraph.Net.Terrain;

// 1. Build a scene graph (Z-up, metres)
var root = new Group { Name = "root" };
var terrain = TerrainGenerator.Fractal(129, 2f, 18, seed: 7);
root.AddChild(TerrainBuilder.Build(terrain, tilesPerSide: 2, levels: 2));

var spinner = new PositionAttitudeTransform { Position = new Vector3(0, 0, 25) };
spinner.AddChild(Shapes.Torus(8, 2, 64, 24, Color.FromHex("#4cc9f0")));
spinner.AddUpdateCallback(new SpinCallback(Vector3.UnitX, 30));
root.AddChild(spinner);

// 2. Load any model OpenSceneGraph understands (.osgt, .3ds, .flt, .obj ...) passed on the command line
bool native = OsgNative.Initialize(); // also lets Registry fall back to OSG plugins
if (args.FirstOrDefault(a => !a.StartsWith("--")) is { } file)
    root.AddChild(OpenSceneGraph.Net.IO.Registry.ReadNodeFile(file));

if (!native)
{
    Console.WriteLine("Native OpenSceneGraph runtime not available on this platform.");
    return;
}

// 3a. Render to PNG
if (args.Contains("--render"))
{
    var b = root.Bound;
    var view = Matrix4x4.CreateLookAt(b.Center + new Vector3(b.Radius, -b.Radius * 1.4f, b.Radius * 0.8f), b.Center, Vector3.UnitZ);
    var img = NativeViewer.RenderToImage(root, 1280, 720, view);
    OpenSceneGraph.Net.IO.PngReader.Write("render.png", img);
    Console.WriteLine("Saved render.png");
    return;
}

// 3b. Or open a real osgViewer window (s = stats, w = wireframe, Esc = quit)
using var viewer = NativeViewer.CreateWindow(1280, 720, "OsgNetConsoleApp");
viewer.SetScene(root);
viewer.AddStatsHandler();
viewer.Run();
