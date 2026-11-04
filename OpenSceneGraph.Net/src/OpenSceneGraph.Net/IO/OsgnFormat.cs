using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.IO;

/// <summary>
/// The OpenSceneGraph.Net native scene format (<c>.osgn</c>): JSON describing the node hierarchy, state sets and
/// geometry (vertex arrays are little-endian base64 for compactness). It plays the role of <c>.osgt</c> for managed
/// code; shared geometry and state sets are written once and referenced by id.
/// </summary>
public sealed class OsgnReaderWriter : IReaderWriter
{
    public const int FormatVersion = 1;

    public string Name => "OpenSceneGraph.Net scene";
    public IReadOnlyList<string> Extensions { get; } = ["osgn"];
    public bool CanRead => true;
    public bool CanWrite => true;

    // ---------------------------------------------------------------- write
    public void WriteNode(Node node, Stream stream)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        w.WriteStartObject();
        w.WriteString("format", "osgn");
        w.WriteNumber("version", FormatVersion);
        w.WriteString("generator", "OpenSceneGraph.Net - Gravicode Studios");
        w.WritePropertyName("root");
        WriteNodeJson(w, node, ids);
        w.WriteEndObject();
    }

    private static void WriteNodeJson(Utf8JsonWriter w, Node n, Dictionary<object, int> ids)
    {
        w.WriteStartObject();
        if (ids.TryGetValue(n, out int existing)) { w.WriteNumber("ref", existing); w.WriteEndObject(); return; }
        int id = ids.Count + 1;
        ids[n] = id;
        w.WriteNumber("id", id);
        w.WriteString("type", n switch
        {
            Geometry => "Geometry",
            PagedLod => "PagedLOD",
            Lod => "LOD",
            Switch => "Switch",
            MatrixTransform => "MatrixTransform",
            PositionAttitudeTransform => "PositionAttitudeTransform",
            AutoTransform => "AutoTransform",
            ProxyNode => "ProxyNode",
            LightSource => "LightSource",
            OccluderNode => "OccluderNode",
            Geode => "Geode",
            _ => "Group",
        });
        if (n.Name is not null) w.WriteString("name", n.Name);
        if (n.NodeMask != 0xFFFFFFFF) w.WriteNumber("nodeMask", n.NodeMask);
        if (!n.CullingActive) w.WriteBoolean("cullingActive", false);
        if (n.StateSet is { } ss) { w.WritePropertyName("stateSet"); WriteStateSet(w, ss, ids); }

        switch (n)
        {
            case Geometry g: WriteGeometry(w, g); break;
            case MatrixTransform mt: WriteFloats(w, "matrix", MatrixToArray(mt.Matrix)); break;
            case PositionAttitudeTransform pat:
                WriteFloats(w, "position", [pat.Position.X, pat.Position.Y, pat.Position.Z]);
                WriteFloats(w, "attitude", [pat.Attitude.X, pat.Attitude.Y, pat.Attitude.Z, pat.Attitude.W]);
                WriteFloats(w, "scale", [pat.Scale.X, pat.Scale.Y, pat.Scale.Z]);
                WriteFloats(w, "pivot", [pat.PivotPoint.X, pat.PivotPoint.Y, pat.PivotPoint.Z]);
                break;
            case AutoTransform at:
                WriteFloats(w, "position", [at.Position.X, at.Position.Y, at.Position.Z]);
                w.WriteBoolean("autoRotate", at.AutoRotateToScreen);
                w.WriteNumber("autoScale", at.AutoScaleToScreen);
                break;
            case LightSource ls: w.WritePropertyName("light"); WriteLight(w, ls.Light); break;
            case OccluderNode on: WriteFloats(w, "occluder", MemoryMarshal.Cast<Vector3, float>(on.Occluder).ToArray()); break;
            case ProxyNode pn:
                w.WriteStartArray("files");
                foreach (var f in pn.FileNames) w.WriteStringValue(f);
                w.WriteEndArray();
                break;
        }

        if (n is Lod lod)
        {
            w.WriteStartArray("ranges");
            foreach (var (min, max) in lod.Ranges) { w.WriteStartArray(); w.WriteNumberValue(min); w.WriteNumberValue(max == float.MaxValue ? -1 : max); w.WriteEndArray(); }
            w.WriteEndArray();
            w.WriteString("rangeMode", lod.RangeMode.ToString());
            if (lod.CenterMode == LodCenterMode.UserDefinedCenter)
            {
                WriteFloats(w, "center", [lod.Center.X, lod.Center.Y, lod.Center.Z]);
                w.WriteNumber("radius", lod.Radius);
            }
            if (lod is PagedLod plod)
            {
                w.WriteStartArray("files");
                foreach (var f in plod.Files) w.WriteStringValue(f.FileName);
                w.WriteEndArray();
            }
        }
        if (n is Switch sw)
        {
            w.WriteStartArray("values");
            for (int i = 0; i < sw.ChildCount; i++) w.WriteBooleanValue(sw.GetValue(i));
            w.WriteEndArray();
        }

        if (n is Group grp and not ProxyNode && grp.ChildCount > 0)
        {
            // PagedLOD: only embedded children are written; file-backed ones are re-paged at runtime
            int count = grp.ChildCount;
            if (grp is PagedLod pl)
            {
                count = 0;
                while (count < grp.ChildCount && (count >= pl.Files.Count || string.IsNullOrEmpty(pl.Files[count].FileName))) count++;
            }
            w.WriteStartArray("children");
            for (int i = 0; i < count; i++) WriteNodeJson(w, grp.GetChild(i), ids);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteGeometry(Utf8JsonWriter w, Geometry g)
    {
        w.WriteString("primitive", g.PrimitiveType.ToString());
        if (g.Usage != BufferUsage.Static) w.WriteString("usage", g.Usage.ToString());
        WriteBlob(w, "positions", g.Positions);
        if (g.Normals is { } n) WriteBlob(w, "normals", n);
        if (g.Colors is { } c) WriteBlob(w, "colors", c);
        if (g.TexCoords is { } t) WriteBlob(w, "texcoords", t);
        if (g.Indices is { } i) WriteBlob(w, "indices", i);
    }

    private static void WriteBlob<T>(Utf8JsonWriter w, string name, T[] data) where T : unmanaged =>
        w.WriteBase64String(name, MemoryMarshal.AsBytes(data.AsSpan()));

    private static void WriteFloats(Utf8JsonWriter w, string name, ReadOnlySpan<float> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static float[] MatrixToArray(Matrix4x4 m) => MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(ref m, 1)).ToArray();

    private static void WriteStateSet(Utf8JsonWriter w, StateSet ss, Dictionary<object, int> ids)
    {
        w.WriteStartObject();
        if (ids.TryGetValue(ss, out int existing)) { w.WriteNumber("ref", existing); w.WriteEndObject(); return; }
        int id = ids.Count + 1;
        ids[ss] = id;
        w.WriteNumber("id", id);
        if (ss.Material is { } m)
        {
            w.WriteStartObject("material");
            WriteFloats(w, "diffuse", [m.Diffuse.X, m.Diffuse.Y, m.Diffuse.Z, m.Diffuse.W]);
            WriteFloats(w, "ambient", [m.Ambient.X, m.Ambient.Y, m.Ambient.Z]);
            WriteFloats(w, "specular", [m.Specular.X, m.Specular.Y, m.Specular.Z]);
            WriteFloats(w, "emission", [m.Emission.X, m.Emission.Y, m.Emission.Z]);
            w.WriteNumber("shininess", m.Shininess);
            w.WriteNumber("metallic", m.Metallic);
            w.WriteNumber("roughness", m.Roughness);
            w.WriteNumber("reflectivity", m.Reflectivity);
            w.WriteEndObject();
        }
        if (ss.Texture?.Image is { } img)
        {
            w.WriteStartObject("texture");
            w.WriteNumber("width", img.Width);
            w.WriteNumber("height", img.Height);
            w.WriteString("wrap", ss.Texture.Wrap.ToString());
            w.WriteBase64String("rgba", img.Pixels);
            w.WriteEndObject();
        }
        if (ss.Lighting is { } l) w.WriteBoolean("lighting", l);
        if (ss.CullFace is { } cf) w.WriteString("cullFace", cf.ToString());
        if (ss.Blend is { } b) w.WriteString("blend", b.ToString());
        if (ss.PolygonMode is { } pm) w.WriteString("polygonMode", pm.ToString());
        if (ss.DepthTest is { } dt) w.WriteBoolean("depthTest", dt);
        if (ss.DepthWrite is { } dw) w.WriteBoolean("depthWrite", dw);
        if (ss.RenderBin is { } rb) w.WriteNumber("renderBin", rb);
        if (ss.LineWidth is { } lw) w.WriteNumber("lineWidth", lw);
        if (ss.PointSize is { } ps) w.WriteNumber("pointSize", ps);
        if (ss.CastShadows is { } cs) w.WriteBoolean("castShadows", cs);
        if (ss.ReceiveShadows is { } rs) w.WriteBoolean("receiveShadows", rs);
        if (ss.Fog is { } fg) w.WriteBoolean("fog", fg);
        if (ss.Override) w.WriteBoolean("override", true);
        w.WriteEndObject();
    }

    private static void WriteLight(Utf8JsonWriter w, Light l)
    {
        w.WriteStartObject();
        w.WriteString("type", l.Type.ToString());
        WriteFloats(w, "color", [l.Color.X, l.Color.Y, l.Color.Z]);
        w.WriteNumber("intensity", l.Intensity);
        WriteFloats(w, "position", [l.Position.X, l.Position.Y, l.Position.Z]);
        WriteFloats(w, "direction", [l.Direction.X, l.Direction.Y, l.Direction.Z]);
        w.WriteNumber("range", l.Range);
        w.WriteNumber("spotCutoff", l.SpotCutoffDegrees);
        w.WriteBoolean("castShadows", l.CastShadows);
        w.WriteEndObject();
    }

    // ---------------------------------------------------------------- read
    public Node ReadNode(Stream stream, ReadOptions options)
    {
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 512 });
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var f) || f.GetString() != "osgn") throw new InvalidDataException("Not an .osgn file.");
        var objs = new Dictionary<int, object>();
        return ReadNodeJson(root.GetProperty("root"), objs);
    }

    private static Node ReadNodeJson(JsonElement e, Dictionary<int, object> objs)
    {
        if (e.TryGetProperty("ref", out var r)) return (Node)objs[r.GetInt32()];
        string type = e.GetProperty("type").GetString()!;
        Node n = type switch
        {
            "Geometry" => ReadGeometry(e),
            "MatrixTransform" => new MatrixTransform(ArrayToMatrix(Floats(e, "matrix"))),
            "PositionAttitudeTransform" => ReadPat(e),
            "AutoTransform" => new AutoTransform
            {
                Position = V3(Floats(e, "position")),
                AutoRotateToScreen = e.GetProperty("autoRotate").GetBoolean(),
                AutoScaleToScreen = e.GetProperty("autoScale").GetSingle(),
            },
            "Switch" => new Switch(),
            "LOD" => new Lod(),
            "PagedLOD" => new PagedLod(),
            "ProxyNode" => new ProxyNode(),
            "LightSource" => new LightSource(ReadLight(e.GetProperty("light"))),
            "OccluderNode" => new OccluderNode { Occluder = MemoryMarshal.Cast<float, Vector3>(Floats(e, "occluder")).ToArray() },
            "Geode" => new Geode(),
            _ => new Group(),
        };
        if (e.TryGetProperty("id", out var id)) objs[id.GetInt32()] = n;
        if (e.TryGetProperty("name", out var name)) n.Name = name.GetString();
        if (e.TryGetProperty("nodeMask", out var mask)) n.NodeMask = mask.GetUInt32();
        if (e.TryGetProperty("cullingActive", out var ca)) n.CullingActive = ca.GetBoolean();
        if (e.TryGetProperty("stateSet", out var sse)) n.StateSet = ReadStateSet(sse, objs);

        if (n is ProxyNode pn && e.TryGetProperty("files", out var pf))
            foreach (var x in pf.EnumerateArray()) pn.FileNames.Add(x.GetString()!);

        if (n is Group g && e.TryGetProperty("children", out var children))
            foreach (var c in children.EnumerateArray()) g.AddChild(ReadNodeJson(c, objs));

        if (n is Lod lod)
        {
            if (e.TryGetProperty("rangeMode", out var rm)) lod.RangeMode = Enum.Parse<LodRangeMode>(rm.GetString()!);
            if (e.TryGetProperty("center", out _))
            {
                lod.CenterMode = LodCenterMode.UserDefinedCenter;
                lod.Center = V3(Floats(e, "center"));
                lod.Radius = e.GetProperty("radius").GetSingle();
            }
            int i = 0;
            foreach (var range in e.GetProperty("ranges").EnumerateArray())
            {
                float max = range[1].GetSingle();
                lod.SetRange(i++, range[0].GetSingle(), max < 0 ? float.MaxValue : max);
            }
            if (lod is PagedLod plod && e.TryGetProperty("files", out var files))
            {
                int k = 0;
                foreach (var file in files.EnumerateArray())
                {
                    var (min, max) = plod.Ranges[k];
                    plod.SetFileName(k, file.GetString() ?? "");
                    plod.SetRange(k, min, max);
                    k++;
                }
            }
        }
        if (n is Switch sw && e.TryGetProperty("values", out var vals))
        {
            int i = 0;
            foreach (var v in vals.EnumerateArray()) if (i < sw.ChildCount) sw.SetValue(i++, v.GetBoolean());
        }
        return n;
    }

    private static PositionAttitudeTransform ReadPat(JsonElement e)
    {
        var a = Floats(e, "attitude");
        return new PositionAttitudeTransform
        {
            Position = V3(Floats(e, "position")),
            Attitude = new Quaternion(a[0], a[1], a[2], a[3]),
            Scale = V3(Floats(e, "scale")),
            PivotPoint = V3(Floats(e, "pivot")),
        };
    }

    private static Geometry ReadGeometry(JsonElement e)
    {
        var g = new Geometry
        {
            PrimitiveType = Enum.Parse<PrimitiveType>(e.GetProperty("primitive").GetString()!),
            Positions = Blob<Vector3>(e, "positions")!,
            Normals = Blob<Vector3>(e, "normals"),
            Colors = Blob<Vector4>(e, "colors"),
            TexCoords = Blob<Vector2>(e, "texcoords"),
            Indices = Blob<uint>(e, "indices"),
        };
        if (e.TryGetProperty("usage", out var u)) g.Usage = Enum.Parse<BufferUsage>(u.GetString()!);
        return g;
    }

    private static T[]? Blob<T>(JsonElement e, string name) where T : unmanaged =>
        e.TryGetProperty(name, out var p) ? MemoryMarshal.Cast<byte, T>(p.GetBytesFromBase64()).ToArray() : null;

    private static float[] Floats(JsonElement e, string name)
    {
        var arr = e.GetProperty(name);
        var result = new float[arr.GetArrayLength()];
        int i = 0;
        foreach (var v in arr.EnumerateArray()) result[i++] = v.GetSingle();
        return result;
    }

    private static Vector3 V3(float[] f) => new(f[0], f[1], f[2]);
    private static Matrix4x4 ArrayToMatrix(float[] f) => MemoryMarshal.Cast<float, Matrix4x4>(f)[0];

    private static StateSet ReadStateSet(JsonElement e, Dictionary<int, object> objs)
    {
        if (e.TryGetProperty("ref", out var r)) return (StateSet)objs[r.GetInt32()];
        var ss = new StateSet();
        objs[e.GetProperty("id").GetInt32()] = ss;
        if (e.TryGetProperty("material", out var m))
        {
            var d = Floats(m, "diffuse");
            ss.Material = new Material
            {
                Diffuse = new Vector4(d[0], d[1], d[2], d[3]),
                Ambient = V3(Floats(m, "ambient")),
                Specular = V3(Floats(m, "specular")),
                Emission = V3(Floats(m, "emission")),
                Shininess = m.GetProperty("shininess").GetSingle(),
                Metallic = m.TryGetProperty("metallic", out var me) ? me.GetSingle() : 0,
                Roughness = m.TryGetProperty("roughness", out var ro) ? ro.GetSingle() : 0.6f,
                Reflectivity = m.TryGetProperty("reflectivity", out var re) ? re.GetSingle() : 0,
            };
        }
        if (e.TryGetProperty("texture", out var t))
        {
            var img = new Image(t.GetProperty("width").GetInt32(), t.GetProperty("height").GetInt32(), t.GetProperty("rgba").GetBytesFromBase64());
            ss.Texture = new Texture2D(img) { Wrap = Enum.Parse<TextureWrap>(t.GetProperty("wrap").GetString()!) };
        }
        if (e.TryGetProperty("lighting", out var l)) ss.Lighting = l.GetBoolean();
        if (e.TryGetProperty("cullFace", out var cf)) ss.CullFace = Enum.Parse<CullFaceMode>(cf.GetString()!);
        if (e.TryGetProperty("blend", out var b)) ss.Blend = Enum.Parse<BlendMode>(b.GetString()!);
        if (e.TryGetProperty("polygonMode", out var pm)) ss.PolygonMode = Enum.Parse<PolygonMode>(pm.GetString()!);
        if (e.TryGetProperty("depthTest", out var dt)) ss.DepthTest = dt.GetBoolean();
        if (e.TryGetProperty("depthWrite", out var dw)) ss.DepthWrite = dw.GetBoolean();
        if (e.TryGetProperty("renderBin", out var rb)) ss.RenderBin = rb.GetInt32();
        if (e.TryGetProperty("lineWidth", out var lw)) ss.LineWidth = lw.GetSingle();
        if (e.TryGetProperty("pointSize", out var ps)) ss.PointSize = ps.GetSingle();
        if (e.TryGetProperty("castShadows", out var cs)) ss.CastShadows = cs.GetBoolean();
        if (e.TryGetProperty("receiveShadows", out var rs)) ss.ReceiveShadows = rs.GetBoolean();
        if (e.TryGetProperty("fog", out var fg)) ss.Fog = fg.GetBoolean();
        if (e.TryGetProperty("override", out var ov)) ss.Override = ov.GetBoolean();
        return ss;
    }

    private static Light ReadLight(JsonElement e) => new()
    {
        Type = Enum.Parse<LightType>(e.GetProperty("type").GetString()!),
        Color = V3(Floats(e, "color")),
        Intensity = e.GetProperty("intensity").GetSingle(),
        Position = V3(Floats(e, "position")),
        Direction = V3(Floats(e, "direction")),
        Range = e.GetProperty("range").GetSingle(),
        SpotCutoffDegrees = e.GetProperty("spotCutoff").GetSingle(),
        CastShadows = e.GetProperty("castShadows").GetBoolean(),
    };
}
