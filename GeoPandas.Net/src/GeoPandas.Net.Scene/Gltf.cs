using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace GeoPandas.Net.Scene;

/// <summary>A node of a glTF scene: a mesh with an optional column-major 4×4 matrix.</summary>
public sealed record SceneNode(Mesh Mesh, double[]? Matrix = null);

/// <summary>
/// glTF 2.0 binary (GLB) writer. Meshes are in local ENU metres (z up) and are written y-up as glTF requires (x, z, −y), which 3D Tiles
/// rotates back. Vertex colours, normals, feature ids (<c>EXT_mesh_features</c>) and per-feature properties (<c>EXT_structural_metadata</c>)
/// are supported; points and lines use <c>KHR_materials_unlit</c>.
/// </summary>
public static class Gltf
{
    private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;
    private const int Float = 5126, UnsignedByte = 5121, UnsignedInt = 5125;

    public static void WriteGlb(string path, params Mesh[] meshes) => File.WriteAllBytes(path, ToGlb(meshes.Select(m => new SceneNode(m)).ToArray()));
    public static void WriteGlb(string path, IReadOnlyList<SceneNode> nodes) => File.WriteAllBytes(path, ToGlb(nodes));
    public static byte[] ToGlb(params Mesh[] meshes) => ToGlb(meshes.Select(m => new SceneNode(m)).ToArray());

    public static byte[] ToGlb(IReadOnlyList<SceneNode> nodes)
    {
        var bin = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();
        var meshesJson = new JsonArray();
        var materials = new JsonArray();
        var nodesJson = new JsonArray();
        var extensionsUsed = new HashSet<string>();
        var schemaClasses = new JsonObject();
        var propertyTables = new JsonArray();

        int View(ReadOnlySpan<byte> data, int? target, int alignment = 4)
        {
            while (bin.Length % alignment != 0) bin.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
            if (target is { } t) view["target"] = t;
            bin.Write(data);
            bufferViews.Add((JsonNode)view);
            return bufferViews.Count - 1;
        }
        int Accessor(int view, int componentType, int count, string type, bool normalized = false, double[]? min = null, double[]? max = null)
        {
            var a = new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (normalized) a["normalized"] = true;
            if (min is not null) a["min"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray());
            if (max is not null) a["max"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray());
            accessors.Add((JsonNode)a);
            return accessors.Count - 1;
        }

        for (int ni = 0; ni < nodes.Count; ni++)
        {
            var m = nodes[ni].Mesh;
            if (m.Positions.Count == 0) continue;
            // positions, y-up
            var pos = new byte[m.Positions.Count * 12];
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < m.Positions.Count; i++)
            {
                var (e, n, u) = m.Positions[i];
                float x = (float)e, y = (float)u, z = (float)-n;
                BinaryPrimitives.WriteSingleLittleEndian(pos.AsSpan(i * 12), x);
                BinaryPrimitives.WriteSingleLittleEndian(pos.AsSpan(i * 12 + 4), y);
                BinaryPrimitives.WriteSingleLittleEndian(pos.AsSpan(i * 12 + 8), z);
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
            var attributes = new JsonObject
            {
                ["POSITION"] = Accessor(View(pos, ArrayBuffer), Float, m.Positions.Count, "VEC3", min: [minX, minY, minZ], max: [maxX, maxY, maxZ])
            };
            if (m.Normals is { Count: > 0 } normals && normals.Count == m.Positions.Count)
            {
                var nb = new byte[normals.Count * 12];
                for (int i = 0; i < normals.Count; i++)
                {
                    var (x, y, z) = normals[i];
                    BinaryPrimitives.WriteSingleLittleEndian(nb.AsSpan(i * 12), x);
                    BinaryPrimitives.WriteSingleLittleEndian(nb.AsSpan(i * 12 + 4), z);
                    BinaryPrimitives.WriteSingleLittleEndian(nb.AsSpan(i * 12 + 8), -y);
                }
                attributes["NORMAL"] = Accessor(View(nb, ArrayBuffer), Float, normals.Count, "VEC3");
            }
            if (m.Colors is { Count: > 0 } colors && colors.Count == m.Positions.Count)
            {
                var cb = new byte[colors.Count * 4];
                for (int i = 0; i < colors.Count; i++) { cb[i * 4] = colors[i].R; cb[i * 4 + 1] = colors[i].G; cb[i * 4 + 2] = colors[i].B; cb[i * 4 + 3] = colors[i].A; }
                attributes["COLOR_0"] = Accessor(View(cb, ArrayBuffer), UnsignedByte, colors.Count, "VEC4", normalized: true);
            }
            JsonObject? primitiveExtensions = null;
            if (m.FeatureIds is { Count: > 0 } ids && ids.Count == m.Positions.Count)
            {
                var fb = new byte[ids.Count * 4];
                for (int i = 0; i < ids.Count; i++) BinaryPrimitives.WriteSingleLittleEndian(fb.AsSpan(i * 4), ids[i]);
                attributes["_FEATURE_ID_0"] = Accessor(View(fb, ArrayBuffer), Float, ids.Count, "SCALAR");
                int featureCount = (int)ids.Max() + 1;
                var featureId = new JsonObject { ["featureCount"] = featureCount, ["attribute"] = 0 };
                if (m.FeatureTable is { } table && table.RowCount >= featureCount)
                {
                    featureId["propertyTable"] = PropertyTable(m.Name, table, featureCount, View, schemaClasses, propertyTables);
                    extensionsUsed.Add("EXT_structural_metadata");
                }
                primitiveExtensions = new JsonObject { ["EXT_mesh_features"] = new JsonObject { ["featureIds"] = new JsonArray(featureId) } };
                extensionsUsed.Add("EXT_mesh_features");
            }
            var primitive = new JsonObject { ["attributes"] = attributes, ["mode"] = (int)m.Mode, ["material"] = materials.Count };
            if (m.Indices.Count > 0)
            {
                var ib = new byte[m.Indices.Count * 4];
                for (int i = 0; i < m.Indices.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(ib.AsSpan(i * 4), m.Indices[i]);
                primitive["indices"] = Accessor(View(ib, ElementArrayBuffer), UnsignedInt, m.Indices.Count, "SCALAR");
            }
            if (primitiveExtensions is not null) primitive["extensions"] = primitiveExtensions;
            var mat = m.Material;
            var material = new JsonObject
            {
                ["name"] = mat.Name,
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorFactor"] = new JsonArray(mat.R, mat.G, mat.B, mat.A), ["metallicFactor"] = mat.Metallic, ["roughnessFactor"] = mat.Roughness
                },
                ["doubleSided"] = mat.DoubleSided
            };
            if (mat.A < 1) material["alphaMode"] = "BLEND";
            if (mat.Unlit || m.Mode != PrimitiveMode.Triangles)
            {
                material["extensions"] = new JsonObject { ["KHR_materials_unlit"] = new JsonObject() };
                extensionsUsed.Add("KHR_materials_unlit");
            }
            materials.Add((JsonNode)material);
            meshesJson.Add((JsonNode)new JsonObject { ["name"] = m.Name, ["primitives"] = new JsonArray(primitive) });
            var node = new JsonObject { ["mesh"] = meshesJson.Count - 1, ["name"] = m.Name };
            if (nodes[ni].Matrix is { } mx) node["matrix"] = new JsonArray(mx.Select(v => (JsonNode)v).ToArray());
            nodesJson.Add((JsonNode)node);
        }
        while (bin.Length % 4 != 0) bin.WriteByte(0);

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "GeoPandas.Net" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(Enumerable.Range(0, nodesJson.Count).Select(i => (JsonNode)i).ToArray()) }),
            ["nodes"] = nodesJson,
            ["meshes"] = meshesJson,
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
        };
        if (extensionsUsed.Count > 0) root["extensionsUsed"] = new JsonArray(extensionsUsed.Order().Select(e => (JsonNode)e).ToArray());
        if (propertyTables.Count > 0)
            root["extensions"] = new JsonObject
            {
                ["EXT_structural_metadata"] = new JsonObject
                {
                    ["schema"] = new JsonObject { ["id"] = "geopandas_net", ["classes"] = schemaClasses },
                    ["propertyTables"] = propertyTables
                }
            };

        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonLen = (json.Length + 3) & ~3;
        int total = 12 + 8 + jsonLen + 8 + (int)bin.Length;
        var glb = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);       // "glTF"
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A); // "JSON"
        json.CopyTo(glb, 20);
        for (int i = json.Length; i < jsonLen; i++) glb[20 + i] = 0x20;
        int binHeader = 20 + jsonLen;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binHeader), (uint)bin.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binHeader + 4), 0x004E4942); // "BIN\0"
        bin.ToArray().CopyTo(glb, binHeader + 8);
        return glb;
    }

    /// <summary>Writes an EXT_structural_metadata class and property table (numbers as FLOAT64, integers as INT64, the rest as strings).</summary>
    private static int PropertyTable(string className, DataFrame table, int count, Func<ReadOnlySpan<byte>, int?, int, int> view,
        JsonObject classes, JsonArray tables)
    {
        string cls = Identifier(className);
        var classProps = new JsonObject();
        var tableProps = new JsonObject();
        foreach (var col in table.Columns)
        {
            string id = Identifier(col.Name);
            if (classProps.ContainsKey(id)) continue;
            var t = col.DataType;
            if (t == typeof(double) || t == typeof(float))
            {
                var b = new byte[count * 8];
                for (int i = 0; i < count; i++) BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(i * 8), col.IsNull(i) ? double.NaN : col.GetDouble(i));
                classProps[id] = new JsonObject { ["name"] = col.Name, ["type"] = "SCALAR", ["componentType"] = "FLOAT64" };
                tableProps[id] = new JsonObject { ["values"] = view(b, null, 8) };
            }
            else if (t == typeof(long) || t == typeof(int))
            {
                var b = new byte[count * 8];
                for (int i = 0; i < count; i++) BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(i * 8), col.IsNull(i) ? 0 : (long)col.GetDouble(i));
                classProps[id] = new JsonObject { ["name"] = col.Name, ["type"] = "SCALAR", ["componentType"] = "INT64" };
                tableProps[id] = new JsonObject { ["values"] = view(b, null, 8) };
            }
            else if (t == typeof(bool))
            {
                var b = new byte[(count + 7) / 8];
                for (int i = 0; i < count; i++) if (col.GetValue(i) is true) b[i / 8] |= (byte)(1 << (i % 8));
                classProps[id] = new JsonObject { ["name"] = col.Name, ["type"] = "BOOLEAN" };
                tableProps[id] = new JsonObject { ["values"] = view(b, null, 8) };
            }
            else
            {
                var strings = new MemoryStream();
                var offsets = new byte[(count + 1) * 4];
                for (int i = 0; i < count; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(offsets.AsSpan(i * 4), (uint)strings.Length);
                    var s = Convert.ToString(col.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    strings.Write(Encoding.UTF8.GetBytes(s));
                }
                BinaryPrimitives.WriteUInt32LittleEndian(offsets.AsSpan(count * 4), (uint)strings.Length);
                classProps[id] = new JsonObject { ["name"] = col.Name, ["type"] = "STRING" };
                tableProps[id] = new JsonObject { ["values"] = view(strings.ToArray(), null, 8), ["stringOffsets"] = view(offsets, null, 8), ["stringOffsetType"] = "UINT32" };
            }
        }
        while (classes.ContainsKey(cls)) cls += "_";
        classes[cls] = new JsonObject { ["properties"] = classProps };
        tables.Add((JsonNode)new JsonObject { ["class"] = cls, ["count"] = count, ["properties"] = tableProps });
        return tables.Count - 1;
    }

    private static string Identifier(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsAsciiDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}
