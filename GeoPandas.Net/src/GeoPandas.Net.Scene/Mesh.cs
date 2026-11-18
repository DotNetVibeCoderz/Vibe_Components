namespace GeoPandas.Net.Scene;

public enum PrimitiveMode { Points = 0, Lines = 1, Triangles = 4 }

/// <summary>A PBR metallic-roughness material.</summary>
public sealed record SceneMaterial(string Name, float R = 0.8f, float G = 0.8f, float B = 0.8f, float A = 1f, float Metallic = 0f, float Roughness = 0.9f,
    bool DoubleSided = true, bool Unlit = false);

/// <summary>
/// A renderable mesh in a local ENU frame (metres, z up): positions, optional normals, RGBA vertex colours and per-vertex feature ids,
/// with triangle/line indices (or none for points).
/// </summary>
public sealed class Mesh
{
    public string Name { get; init; } = "mesh";
    public PrimitiveMode Mode { get; init; } = PrimitiveMode.Triangles;
    public List<(double E, double N, double U)> Positions { get; } = [];
    public List<(float X, float Y, float Z)>? Normals { get; set; }
    public List<(byte R, byte G, byte B, byte A)>? Colors { get; set; }
    public List<uint>? FeatureIds { get; set; }
    public List<uint> Indices { get; } = [];
    public SceneMaterial Material { get; init; } = new("default");
    /// <summary>Per-feature attributes (EXT_structural_metadata property table), one row per feature id.</summary>
    public DataFrame? FeatureTable { get; set; }

    public int VertexCount => Positions.Count;
    public int TriangleCount => Mode == PrimitiveMode.Triangles ? Indices.Count / 3 : 0;

    public (double MinE, double MinN, double MinU, double MaxE, double MaxN, double MaxU) Bounds()
    {
        double a = double.MaxValue, b = double.MaxValue, c = double.MaxValue, d = double.MinValue, e = double.MinValue, f = double.MinValue;
        foreach (var p in Positions)
        {
            a = Math.Min(a, p.E); b = Math.Min(b, p.N); c = Math.Min(c, p.U);
            d = Math.Max(d, p.E); e = Math.Max(e, p.N); f = Math.Max(f, p.U);
        }
        return (a, b, c, d, e, f);
    }

    /// <summary>Computes smooth (area-weighted) vertex normals for triangle meshes.</summary>
    public Mesh ComputeNormals()
    {
        if (Mode != PrimitiveMode.Triangles) return this;
        var n = new double[Positions.Count * 3];
        for (int t = 0; t + 2 < Indices.Count; t += 3)
        {
            var a = Positions[(int)Indices[t]]; var b = Positions[(int)Indices[t + 1]]; var c = Positions[(int)Indices[t + 2]];
            double ux = b.E - a.E, uy = b.N - a.N, uz = b.U - a.U, vx = c.E - a.E, vy = c.N - a.N, vz = c.U - a.U;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            foreach (var k in new[] { Indices[t], Indices[t + 1], Indices[t + 2] }) { n[k * 3] += cx; n[k * 3 + 1] += cy; n[k * 3 + 2] += cz; }
        }
        Normals = [];
        for (int i = 0; i < Positions.Count; i++)
        {
            double l = Math.Sqrt(n[i * 3] * n[i * 3] + n[i * 3 + 1] * n[i * 3 + 1] + n[i * 3 + 2] * n[i * 3 + 2]);
            Normals.Add(l > 0 ? ((float)(n[i * 3] / l), (float)(n[i * 3 + 1] / l), (float)(n[i * 3 + 2] / l)) : (0f, 0f, 1f));
        }
        return this;
    }

    /// <summary>Signed volume of a closed triangle mesh (m³), by the divergence theorem.</summary>
    public double Volume()
    {
        double v = 0;
        for (int t = 0; t + 2 < Indices.Count; t += 3)
        {
            var a = Positions[(int)Indices[t]]; var b = Positions[(int)Indices[t + 1]]; var c = Positions[(int)Indices[t + 2]];
            v += a.E * (b.N * c.U - b.U * c.N) - a.N * (b.E * c.U - b.U * c.E) + a.U * (b.E * c.N - b.N * c.E);
        }
        return v / 6;
    }

    /// <summary>Appends another mesh of the same mode (vertex attributes present in either are filled with defaults).</summary>
    public void Append(Mesh other)
    {
        uint offset = (uint)Positions.Count;
        bool normals = Normals is not null || other.Normals is not null, colors = Colors is not null || other.Colors is not null, ids = FeatureIds is not null || other.FeatureIds is not null;
        if (normals) { Normals ??= Enumerable.Repeat((0f, 0f, 1f), Positions.Count).ToList(); Normals.AddRange(other.Normals ?? Enumerable.Repeat((0f, 0f, 1f), other.Positions.Count)); }
        if (colors) { Colors ??= Enumerable.Repeat(((byte)255, (byte)255, (byte)255, (byte)255), Positions.Count).ToList(); Colors.AddRange(other.Colors ?? Enumerable.Repeat(((byte)255, (byte)255, (byte)255, (byte)255), other.Positions.Count)); }
        if (ids) { FeatureIds ??= Enumerable.Repeat(0u, Positions.Count).ToList(); FeatureIds.AddRange(other.FeatureIds ?? Enumerable.Repeat(0u, other.Positions.Count)); }
        Positions.AddRange(other.Positions);
        Indices.AddRange(other.Indices.Select(i => i + offset));
    }
}
