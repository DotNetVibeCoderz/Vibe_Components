using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Electrical.Visualization;

namespace Electrical.DigitalTwin.ThreeD;

/// <summary>
/// Writes a <see cref="GridScene3D"/> as binary glTF 2.0 (GLB, Khronos glTF 2.0 specification §4.4): one node, mesh and PBR material per
/// scene object, current colours as <c>baseColorFactor</c> (linear), alarm glow as <c>emissiveFactor</c>, and the network link
/// (kind, bus id, branch index, label) in node <c>extras</c>. ThreeNet imports glTF but has no exporter, so this writer is self-contained.
/// </summary>
public static class GlbExporter
{
    /// <summary>GLB magic "glTF" (little-endian 0x46546C67).</summary>
    public const uint Magic = 0x46546C67;

    public static byte[] Export(GridScene3D scene)
    {
        var bin = new MemoryStream();
        var objs = scene.Objects.Where(o => o.Mesh.VertexCount > 0 && o.Mesh.Indices.Count > 0).ToList();
        var views = new List<(int Offset, int Length, int Target)>();
        var accessors = new List<Action<Utf8JsonWriter>>();

        foreach (var o in objs)
        {
            var m = o.Mesh;
            int posView = WriteView(bin, views, 34962, w => { foreach (var p in m.Positions) WriteVec(w, p); });
            int nrmView = WriteView(bin, views, 34962, w => { foreach (var n in m.Normals) WriteVec(w, n); });
            int idxView = WriteView(bin, views, 34963, w => { foreach (var i in m.Indices) w.Write(i); });
            var (lo, hi) = m.Bounds();
            int vc = m.VertexCount, ic = m.Indices.Count;
            accessors.Add(w => Accessor(w, posView, 5126, vc, "VEC3", lo, hi));
            accessors.Add(w => Accessor(w, nrmView, 5126, vc, "VEC3", null, null));
            accessors.Add(w => Accessor(w, idxView, 5125, ic, "SCALAR", null, null));
        }
        while (bin.Length % 4 != 0) bin.WriteByte(0);

        var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", "Electrical.Net DigitalTwin.ThreeD");
            w.WriteString("copyright", "Made by Gravicode Studios, led by Kang Fadhil");
            w.WriteEndObject();
            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            w.WriteString("name", scene.Network.Name);
            w.WriteStartArray("nodes");
            for (int i = 0; i < objs.Count; i++) w.WriteNumberValue(i);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                w.WriteStartObject();
                w.WriteString("name", o.Name);
                w.WriteNumber("mesh", i);
                w.WriteStartObject("extras");
                w.WriteString("kind", o.Kind.ToString());
                if (o.BusId is int b) w.WriteNumber("busId", b);
                if (o.BranchIndex is int k) w.WriteNumber("branchIndex", k);
                if (o.Label is not null) w.WriteString("label", o.Label);
                w.WriteBoolean("alarm", o.Alarm);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("meshes");
            for (int i = 0; i < objs.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("name", objs[i].Name);
                w.WriteStartArray("primitives");
                w.WriteStartObject();
                w.WriteStartObject("attributes");
                w.WriteNumber("POSITION", 3 * i);
                w.WriteNumber("NORMAL", 3 * i + 1);
                w.WriteEndObject();
                w.WriteNumber("indices", 3 * i + 2);
                w.WriteNumber("material", i);
                w.WriteNumber("mode", 4);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("materials");
            foreach (var o in objs)
            {
                var c = ToLinear(o.Color);
                w.WriteStartObject();
                w.WriteString("name", o.Name);
                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteStartArray("baseColorFactor");
                w.WriteNumberValue(c.X); w.WriteNumberValue(c.Y); w.WriteNumberValue(c.Z); w.WriteNumberValue(1);
                w.WriteEndArray();
                w.WriteNumber("metallicFactor", o.Metallic);
                w.WriteNumber("roughnessFactor", o.Roughness);
                w.WriteEndObject();
                if (o.Emission > 0)
                {
                    var e = ToLinear(GridScene3D.AlarmColor(o.AlarmSeverity)) * o.Emission;
                    w.WriteStartArray("emissiveFactor");
                    w.WriteNumberValue(e.X); w.WriteNumberValue(e.Y); w.WriteNumberValue(e.Z);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("accessors");
            foreach (var a in accessors) a(w);
            w.WriteEndArray();

            w.WriteStartArray("bufferViews");
            foreach (var (off, len, target) in views)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", off);
                w.WriteNumber("byteLength", len);
                w.WriteNumber("target", target);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteNumber("byteLength", bin.Length);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        while (json.Length % 4 != 0) json.WriteByte((byte)' ');

        var outp = new MemoryStream();
        var u32 = new byte[4];
        void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(u32, v); outp.Write(u32); }
        uint total = (uint)(12 + 8 + json.Length + 8 + bin.Length);
        U32(Magic); U32(2); U32(total);
        U32((uint)json.Length); U32(0x4E4F534A); // "JSON"
        json.Position = 0; json.CopyTo(outp);
        U32((uint)bin.Length); U32(0x004E4942); // "BIN\0"
        bin.Position = 0; bin.CopyTo(outp);
        return outp.ToArray();
    }

    public static void Export(GridScene3D scene, string path) => File.WriteAllBytes(path, Export(scene));

    /// <summary>Parses the header and JSON chunk of a GLB (for validation): version, total length and the glTF JSON text.</summary>
    public static (uint Version, uint Length, string Json) ReadHeader(ReadOnlySpan<byte> glb)
    {
        if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != Magic) throw new InvalidDataException("Not a GLB file.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(glb[4..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(glb[8..]);
        int jsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(glb[16..]) != 0x4E4F534A) throw new InvalidDataException("First GLB chunk is not JSON.");
        return (version, length, Encoding.UTF8.GetString(glb.Slice(20, jsonLen)).TrimEnd(' '));
    }

    /// <summary>sRGB colour → linear RGB (IEC 61966-2-1), as glTF material factors require.</summary>
    public static Vector3 ToLinear(Rgba c)
    {
        static float L(byte v)
        {
            double s = v / 255.0;
            return (float)(s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4));
        }
        return new Vector3(L(c.R), L(c.G), L(c.B));
    }

    private static int WriteView(MemoryStream bin, List<(int, int, int)> views, int target, Action<BinaryWriter> write)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        int start = (int)bin.Length;
        using (var w = new BinaryWriter(bin, Encoding.UTF8, leaveOpen: true)) write(w);
        views.Add((start, (int)bin.Length - start, target));
        return views.Count - 1;
    }

    private static void WriteVec(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    private static void Accessor(Utf8JsonWriter w, int view, int componentType, int count, string type, Vector3? min, Vector3? max)
    {
        w.WriteStartObject();
        w.WriteNumber("bufferView", view);
        w.WriteNumber("componentType", componentType);
        w.WriteNumber("count", count);
        w.WriteString("type", type);
        if (min is { } lo && max is { } hi)
        {
            w.WriteStartArray("min"); w.WriteNumberValue(lo.X); w.WriteNumberValue(lo.Y); w.WriteNumberValue(lo.Z); w.WriteEndArray();
            w.WriteStartArray("max"); w.WriteNumberValue(hi.X); w.WriteNumberValue(hi.Y); w.WriteNumberValue(hi.Z); w.WriteEndArray();
        }
        w.WriteEndObject();
    }
}
