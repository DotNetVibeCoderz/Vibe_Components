using System.Globalization;
using System.Numerics;
using System.Text;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.IO;

internal static class FormatUtil
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);
    public static string S(float f) => f.ToString("R", Inv);

    /// <summary>Flattens a scene into world-space triangle geometries.</summary>
    public static IEnumerable<(Geometry G, Matrix4x4 World)> CollectGeometries(Node root)
    {
        var v = new GeometryCollector();
        root.Accept(v);
        return v.Items;
    }

    private sealed class GeometryCollector() : MatrixTrackingVisitor(TraversalMode.AllChildren)
    {
        public List<(Geometry, Matrix4x4)> Items { get; } = [];
        public override void Apply(Drawable drawable)
        {
            if (drawable is Geometry g && g.PrimitiveType is PrimitiveType.Triangles or PrimitiveType.TriangleStrip) Items.Add((g, CurrentMatrix));
        }
    }
}

/// <summary>Wavefront OBJ + MTL reader/writer (<c>osgdb_obj</c>).</summary>
public sealed class ObjReaderWriter : IReaderWriter
{
    public string Name => "Wavefront OBJ";
    public IReadOnlyList<string> Extensions { get; } = ["obj"];
    public bool CanRead => true;
    public bool CanWrite => true;

    private sealed class Builder
    {
        public readonly List<Vector3> Pos = [];
        public readonly List<Vector3> Nor = [];
        public readonly List<Vector2> Uv = [];
        public readonly List<uint> Idx = [];
        public readonly Dictionary<(int, int, int), uint> Map = [];
        public bool HasNormals, HasUv;
    }

    public Node ReadNode(Stream stream, ReadOptions options)
    {
        var v = new List<Vector3>();
        var vn = new List<Vector3>();
        var vt = new List<Vector2>();
        var vcol = new List<Vector4>();
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        var textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        string current = "";
        var reader = new StreamReader(stream);
        Span<Range> parts = stackalloc Range[64];
        Span<uint> faceBuffer = stackalloc uint[60];

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#') continue;
            int n = span.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
            if (n == 0) continue;
            var key = span[parts[0]];

            if (key.SequenceEqual("v") && n >= 4)
            {
                v.Add(new(P(span[parts[1]]), P(span[parts[2]]), P(span[parts[3]])));
                if (n >= 7) vcol.Add(new(P(span[parts[4]]), P(span[parts[5]]), P(span[parts[6]]), 1));
            }
            else if (key.SequenceEqual("vn") && n >= 4) vn.Add(new(P(span[parts[1]]), P(span[parts[2]]), P(span[parts[3]])));
            else if (key.SequenceEqual("vt") && n >= 3) vt.Add(new(P(span[parts[1]]), P(span[parts[2]])));
            else if (key.SequenceEqual("usemtl") && n >= 2) current = span[parts[1].Start..].ToString();
            else if (key.SequenceEqual("mtllib") && n >= 2 && options.DatabasePath is { } dir)
            {
                var mtl = Path.Combine(dir, span[parts[1].Start..].ToString());
                if (File.Exists(mtl)) ReadMtl(mtl, materials, textures, options);
            }
            else if (key.SequenceEqual("f") && n >= 4)
            {
                if (!builders.TryGetValue(current, out var b)) builders[current] = b = new Builder();
                var face = faceBuffer[..System.Math.Min(n - 1, 60)];
                for (int i = 1; i < n && i - 1 < face.Length; i++) face[i - 1] = Vertex(b, span[parts[i]], v, vt, vn);
                for (int i = 1; i + 1 < face.Length; i++) { b.Idx.Add(face[0]); b.Idx.Add(face[i]); b.Idx.Add(face[i + 1]); }
            }
        }

        var root = new Group();
        foreach (var (mat, b) in builders)
        {
            if (b.Idx.Count == 0) continue;
            var g = new Geometry
            {
                Name = string.IsNullOrEmpty(mat) ? "obj" : mat,
                Positions = b.Pos.ToArray(),
                Normals = b.HasNormals ? b.Nor.ToArray() : null,
                TexCoords = b.HasUv ? b.Uv.ToArray() : null,
                Indices = b.Idx.ToArray(),
            };
            if (vcol.Count == v.Count && vcol.Count > 0)
            {
                // vertex colors follow the position index; rebuild per unique vertex
                var cols = new Vector4[g.Positions.Length];
                foreach (var ((pi, _, _), idx) in b.Map) cols[idx] = vcol[pi];
                g.Colors = cols;
            }
            if (g.Normals is null && options.GenerateNormals) g.ComputeNormals();
            if (materials.TryGetValue(mat, out var m))
            {
                var ss = g.GetOrCreateStateSet();
                ss.Material = m;
                if (textures.TryGetValue(mat, out var t)) ss.Texture = t;
            }
            root.AddChild(g);
        }
        return root.ChildCount == 1 ? root.GetChild(0) : root;
    }

    private static float P(ReadOnlySpan<char> s) => float.Parse(s, NumberStyles.Float, FormatUtil.Inv);

    private static uint Vertex(Builder b, ReadOnlySpan<char> token, List<Vector3> v, List<Vector2> vt, List<Vector3> vn)
    {
        int pi = 0, ti = 0, ni = 0;
        int s1 = token.IndexOf('/');
        if (s1 < 0) pi = int.Parse(token, FormatUtil.Inv);
        else
        {
            pi = int.Parse(token[..s1], FormatUtil.Inv);
            var rest = token[(s1 + 1)..];
            int s2 = rest.IndexOf('/');
            if (s2 < 0) { if (!rest.IsEmpty) ti = int.Parse(rest, FormatUtil.Inv); }
            else
            {
                if (s2 > 0) ti = int.Parse(rest[..s2], FormatUtil.Inv);
                if (s2 + 1 < rest.Length) ni = int.Parse(rest[(s2 + 1)..], FormatUtil.Inv);
            }
        }
        // negative indices are relative to the end of the current list
        pi = pi < 0 ? v.Count + pi : pi - 1;
        ti = ti < 0 ? vt.Count + ti : ti - 1;
        ni = ni < 0 ? vn.Count + ni : ni - 1;

        var key = (pi, ti, ni);
        if (b.Map.TryGetValue(key, out uint idx)) return idx;
        idx = (uint)b.Pos.Count;
        b.Pos.Add((uint)pi < (uint)v.Count ? v[pi] : Vector3.Zero);
        b.Uv.Add((uint)ti < (uint)vt.Count ? vt[ti] : Vector2.Zero);
        b.Nor.Add((uint)ni < (uint)vn.Count ? vn[ni] : Vector3.UnitZ);
        b.HasUv |= ti >= 0 && ti < vt.Count;
        b.HasNormals |= ni >= 0 && ni < vn.Count;
        b.Map[key] = idx;
        return idx;
    }

    private static void ReadMtl(string path, Dictionary<string, Material> mats, Dictionary<string, Texture2D> texs, ReadOptions options)
    {
        Material? m = null;
        string? name = null;
        foreach (var raw in File.ReadLines(path))
        {
            var p = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p[0].StartsWith('#')) continue;
            switch (p[0])
            {
                case "newmtl": name = string.Join(' ', p[1..]); mats[name] = m = new Material(); break;
                case "Kd" when m is not null && p.Length >= 4: m.Diffuse = new(FormatUtil.F(p[1]), FormatUtil.F(p[2]), FormatUtil.F(p[3]), m.Diffuse.W); break;
                case "Ka" when m is not null && p.Length >= 4: m.Ambient = new(FormatUtil.F(p[1]), FormatUtil.F(p[2]), FormatUtil.F(p[3])); break;
                case "Ks" when m is not null && p.Length >= 4: m.Specular = new(FormatUtil.F(p[1]), FormatUtil.F(p[2]), FormatUtil.F(p[3])); break;
                case "Ke" when m is not null && p.Length >= 4: m.Emission = new(FormatUtil.F(p[1]), FormatUtil.F(p[2]), FormatUtil.F(p[3])); break;
                case "Ns" when m is not null: m.Shininess = FormatUtil.F(p[1]); break;
                case "d" when m is not null: m.Diffuse = m.Diffuse with { W = FormatUtil.F(p[1]) }; break;
                case "Tr" when m is not null: m.Diffuse = m.Diffuse with { W = 1 - FormatUtil.F(p[1]) }; break;
                case "map_Kd" when name is not null && options.LoadTextures:
                    var img = Registry.TryReadImageFile(Path.Combine(Path.GetDirectoryName(path)!, p[^1]));
                    if (img is not null) texs[name] = new Texture2D(img);
                    break;
            }
        }
    }

    public void WriteNode(Node node, Stream stream)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        w.WriteLine("# OpenSceneGraph.Net OBJ export - Gravicode Studios");
        int baseIndex = 1;
        int part = 0;
        foreach (var (g, world) in FormatUtil.CollectGeometries(node))
        {
            w.WriteLine($"o {(g.Name ?? "part").Replace(' ', '_')}_{part++}");
            var normals = g.Normals ?? MeshUtil.ComputeSmoothNormals(g.Positions, g.Indices, g.PrimitiveType);
            Matrix4x4.Invert(world, out var inv);
            var nm = Matrix4x4.Transpose(inv);
            foreach (var p in g.Positions) { var t = Vector3.Transform(p, world); w.WriteLine($"v {FormatUtil.S(t.X)} {FormatUtil.S(t.Y)} {FormatUtil.S(t.Z)}"); }
            foreach (var n in normals) { var t = Vector3.Normalize(Vector3.TransformNormal(n, nm)); w.WriteLine($"vn {FormatUtil.S(t.X)} {FormatUtil.S(t.Y)} {FormatUtil.S(t.Z)}"); }
            bool uv = g.TexCoords is not null;
            if (uv) foreach (var t in g.TexCoords!) w.WriteLine($"vt {FormatUtil.S(t.X)} {FormatUtil.S(t.Y)}");
            foreach (var (a, b, c) in g.EnumerateTriangles())
            {
                int A = a + baseIndex, B = b + baseIndex, C = c + baseIndex;
                w.WriteLine(uv ? $"f {A}/{A}/{A} {B}/{B}/{B} {C}/{C}/{C}" : $"f {A}//{A} {B}//{B} {C}//{C}");
            }
            baseIndex += g.Positions.Length;
        }
    }
}

/// <summary>STL reader/writer (ASCII and binary read, binary write) (<c>osgdb_stl</c>).</summary>
public sealed class StlReaderWriter : IReaderWriter
{
    public string Name => "Stereolithography";
    public IReadOnlyList<string> Extensions { get; } = ["stl"];
    public bool CanRead => true;
    public bool CanWrite => true;

    public Node ReadNode(Stream stream, ReadOptions options)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        var pos = new List<Vector3>();
        var nor = new List<Vector3>();

        bool binary = data.Length >= 84 && BitConverter.ToUInt32(data[80..84]) is var count && 84 + (long)count * 50 == data.Length;
        if (binary)
        {
            int tris = (int)BitConverter.ToUInt32(data[80..84]);
            for (int t = 0; t < tris; t++)
            {
                var rec = data.Slice(84 + t * 50, 50);
                var n = Read3(rec, 0);
                for (int k = 0; k < 3; k++) { pos.Add(Read3(rec, 12 + k * 12)); nor.Add(n); }
            }
        }
        else
        {
            Vector3 n = Vector3.UnitZ;
            foreach (var raw in Encoding.ASCII.GetString(data).Split('\n'))
            {
                var p = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 5 && p[0] == "facet") n = new(FormatUtil.F(p[2]), FormatUtil.F(p[3]), FormatUtil.F(p[4]));
                else if (p.Length >= 4 && p[0] == "vertex") { pos.Add(new(FormatUtil.F(p[1]), FormatUtil.F(p[2]), FormatUtil.F(p[3]))); nor.Add(n); }
            }
        }

        // STL files often have zero normals; recompute per face in that case
        var normals = nor.ToArray();
        var positions = pos.ToArray();
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            if (normals[i].LengthSquared() > 1e-8f) continue;
            var fn = Vector3.Cross(positions[i + 1] - positions[i], positions[i + 2] - positions[i]);
            fn = fn.LengthSquared() > 0 ? Vector3.Normalize(fn) : Vector3.UnitZ;
            normals[i] = normals[i + 1] = normals[i + 2] = fn;
        }
        return new Geometry { Name = "stl", Positions = positions, Normals = normals };

        static Vector3 Read3(ReadOnlySpan<byte> b, int o) =>
            new(BitConverter.ToSingle(b[o..]), BitConverter.ToSingle(b[(o + 4)..]), BitConverter.ToSingle(b[(o + 8)..]));
    }

    public void WriteNode(Node node, Stream stream)
    {
        var tris = new List<(Vector3, Vector3, Vector3)>();
        foreach (var (g, world) in FormatUtil.CollectGeometries(node))
            foreach (var (a, b, c) in g.EnumerateTriangles())
                tris.Add((Vector3.Transform(g.Positions[a], world), Vector3.Transform(g.Positions[b], world), Vector3.Transform(g.Positions[c], world)));

        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("OpenSceneGraph.Net binary STL - Gravicode Studios").CopyTo(header, 0);
        w.Write(header);
        w.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris)
        {
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero;
            foreach (var v in (ReadOnlySpan<Vector3>)[n, a, b, c]) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write((ushort)0);
        }
    }
}

/// <summary>Stanford PLY reader (ASCII and binary little-endian; vertex position/normal/color, polygon faces).</summary>
public sealed class PlyReader : IReaderWriter
{
    public string Name => "Stanford PLY";
    public IReadOnlyList<string> Extensions { get; } = ["ply"];
    public bool CanRead => true;
    public bool CanWrite => false;
    public void WriteNode(Node node, Stream stream) => throw new NotSupportedException();

    private sealed record Prop(string Name, string Type, string? CountType);

    public Node ReadNode(Stream stream, ReadOptions options)
    {
        // header is ASCII lines until "end_header"
        var headerLines = new List<string>();
        var sb = new StringBuilder();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new InvalidDataException("Unexpected end of PLY header.");
            if (b == '\n')
            {
                var l = sb.ToString().TrimEnd('\r');
                sb.Clear();
                headerLines.Add(l);
                if (l == "end_header") break;
            }
            else sb.Append((char)b);
        }
        if (headerLines.Count == 0 || headerLines[0] != "ply") throw new InvalidDataException("Not a PLY file.");

        string format = "ascii";
        var elements = new List<(string Name, int Count, List<Prop> Props)>();
        foreach (var l in headerLines)
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0] == "format") format = p[1];
            else if (p[0] == "element") elements.Add((p[1], int.Parse(p[2], FormatUtil.Inv), []));
            else if (p[0] == "property" && elements.Count > 0)
                elements[^1].Props.Add(p[1] == "list" ? new Prop(p[4], p[3], p[2]) : new Prop(p[2], p[1], null));
        }
        if (format is not ("ascii" or "binary_little_endian")) throw new NotSupportedException($"PLY format '{format}' is not supported.");

        bool ascii = format == "ascii";
        var tokens = ascii ? new StreamReader(stream).ReadToEnd().Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries) : null;
        int ti = 0;
        var br = ascii ? null : new BinaryReader(stream);

        double Read(string type)
        {
            if (ascii) return double.Parse(tokens![ti++], FormatUtil.Inv);
            return type switch
            {
                "char" or "int8" => br!.ReadSByte(),
                "uchar" or "uint8" => br!.ReadByte(),
                "short" or "int16" => br!.ReadInt16(),
                "ushort" or "uint16" => br!.ReadUInt16(),
                "int" or "int32" => br!.ReadInt32(),
                "uint" or "uint32" => br!.ReadUInt32(),
                "float" or "float32" => br!.ReadSingle(),
                "double" or "float64" => br!.ReadDouble(),
                _ => throw new InvalidDataException($"Unknown PLY type {type}"),
            };
        }

        var pos = new List<Vector3>();
        var nor = new List<Vector3>();
        var col = new List<Vector4>();
        var idx = new List<uint>();
        var faceScratch = new uint[16];
        foreach (var (name, count, props) in elements)
        {
            for (int i = 0; i < count; i++)
            {
                if (name == "vertex")
                {
                    float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, r = 1, g = 1, b = 1, a = 1;
                    bool hasN = false, hasC = false;
                    foreach (var p in props)
                    {
                        if (p.CountType is not null) { int c = (int)Read(p.CountType); for (int k = 0; k < c; k++) Read(p.Type); continue; }
                        double v = Read(p.Type);
                        float colorScale = p.Type is "uchar" or "uint8" ? 1 / 255f : 1;
                        switch (p.Name)
                        {
                            case "x": x = (float)v; break;
                            case "y": y = (float)v; break;
                            case "z": z = (float)v; break;
                            case "nx": nx = (float)v; hasN = true; break;
                            case "ny": ny = (float)v; break;
                            case "nz": nz = (float)v; break;
                            case "red": r = (float)v * colorScale; hasC = true; break;
                            case "green": g = (float)v * colorScale; break;
                            case "blue": b = (float)v * colorScale; break;
                            case "alpha": a = (float)v * colorScale; break;
                        }
                    }
                    pos.Add(new(x, y, z));
                    if (hasN) nor.Add(new(nx, ny, nz));
                    if (hasC) col.Add(new(r, g, b, a));
                }
                else
                {
                    foreach (var p in props)
                    {
                        if (p.CountType is null) { Read(p.Type); continue; }
                        int c = (int)Read(p.CountType);
                        if (faceScratch.Length < c) faceScratch = new uint[c];
                        var f = faceScratch.AsSpan(0, c);
                        for (int k = 0; k < c; k++) f[k] = (uint)Read(p.Type);
                        if (name == "face" && p.Name is "vertex_indices" or "vertex_index")
                            for (int k = 1; k + 1 < c; k++) { idx.Add(f[0]); idx.Add(f[k]); idx.Add(f[k + 1]); }
                    }
                }
            }
        }

        var geom = new Geometry
        {
            Name = "ply",
            Positions = pos.ToArray(),
            Normals = nor.Count == pos.Count ? nor.ToArray() : null,
            Colors = col.Count == pos.Count ? col.ToArray() : null,
            Indices = idx.Count > 0 ? idx.ToArray() : null,
            PrimitiveType = idx.Count > 0 ? PrimitiveType.Triangles : PrimitiveType.Points,
        };
        if (geom.PrimitiveType == PrimitiveType.Points) geom.GetOrCreateStateSet().Lighting = false;
        else if (geom.Normals is null && options.GenerateNormals) geom.ComputeNormals();
        return geom;
    }
}
