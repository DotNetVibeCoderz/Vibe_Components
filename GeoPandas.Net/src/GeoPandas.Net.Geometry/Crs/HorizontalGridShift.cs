using System.Buffers.Binary;
using System.Text;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>One rectangular subgrid of a horizontal shift grid (degrees, east-positive; shifts in arc-seconds, east/north-positive).</summary>
public sealed class ShiftSubgrid
{
    public ShiftSubgrid(string name, string? parent, double south, double north, double west, double east, double latInc, double lonInc, float[] latShift, float[] lonShift)
    {
        Name = name; Parent = parent; South = south; North = north; West = west; East = east; LatInc = latInc; LonInc = lonInc;
        Rows = (int)Math.Round((north - south) / latInc) + 1;
        Cols = (int)Math.Round((east - west) / lonInc) + 1;
        if (latShift.Length != Rows * Cols || lonShift.Length != Rows * Cols) throw new ArgumentException($"Subgrid '{name}' needs {Rows}×{Cols} values.");
        LatShift = latShift; LonShift = lonShift;
    }

    public string Name { get; }
    public string? Parent { get; }
    public double South { get; }
    public double North { get; }
    public double West { get; }
    public double East { get; }
    public double LatInc { get; }
    public double LonInc { get; }
    public int Rows { get; }
    public int Cols { get; }
    /// <summary>Row-major from south-west: index = row·Cols + col (arc-seconds).</summary>
    public float[] LatShift { get; }
    public float[] LonShift { get; }

    public bool Contains(double lon, double lat) => lat >= South - 1e-12 && lat <= North + 1e-12 && lon >= West - 1e-12 && lon <= East + 1e-12;
    public double Area => (North - South) * (East - West);

    /// <summary>Bilinear interpolation of the shift (arc-seconds) at a point inside the subgrid.</summary>
    public (double DLatSec, double DLonSec) Interpolate(double lon, double lat)
    {
        double fx = Math.Clamp((lon - West) / LonInc, 0, Cols - 1), fy = Math.Clamp((lat - South) / LatInc, 0, Rows - 1);
        int c = Math.Min((int)fx, Cols - 2), r = Math.Min((int)fy, Rows - 2);
        if (Cols == 1) c = 0;
        if (Rows == 1) r = 0;
        double tx = fx - c, ty = fy - r;
        double B(float[] a)
        {
            int c1 = Math.Min(c + 1, Cols - 1), r1 = Math.Min(r + 1, Rows - 1);
            double v00 = a[r * Cols + c], v01 = a[r * Cols + c1], v10 = a[r1 * Cols + c], v11 = a[r1 * Cols + c1];
            return v00 * (1 - tx) * (1 - ty) + v01 * tx * (1 - ty) + v10 * (1 - tx) * ty + v11 * tx * ty;
        }
        return (B(LatShift), B(LonShift));
    }
}

/// <summary>
/// Grid-based horizontal datum shift (NTv2 <c>.gsb</c>): NAD27→NAD83 (Canada NTv2_0), AGD66/AGD84→GDA94, GDA94→GDA2020,
/// ED50→ETRS89 (Spain, Portugal), DHDN→ETRS89 (BeTA2007), NZGD49→NZGD2000, SAD69→SIRGAS2000… Bilinear interpolation on the
/// finest subgrid that contains the point, as in PROJ <c>hgridshift</c>. Points outside the grid are left unchanged.
/// </summary>
public sealed class HorizontalGridShift
{
    private const double Rad = Math.PI / 180;

    public HorizontalGridShift(string name, IReadOnlyList<ShiftSubgrid> subgrids, string? fromDatum = null, string? toDatum = null)
    {
        Name = name; Subgrids = subgrids; FromDatum = fromDatum; ToDatum = toDatum;
    }

    public string Name { get; }
    public string? FromDatum { get; }
    public string? ToDatum { get; }
    public IReadOnlyList<ShiftSubgrid> Subgrids { get; }

    private ShiftSubgrid? Find(double lon, double lat)
    {
        ShiftSubgrid? best = null;
        foreach (var g in Subgrids)
            if (g.Contains(lon, lat) && (best is null || g.Area < best.Area)) best = g;
        return best;
    }

    /// <summary>Shift at a point in degrees (dLon, dLat), or null outside the grid.</summary>
    public (double DLon, double DLat)? ShiftAt(double lonDeg, double latDeg)
    {
        var g = Find(lonDeg, latDeg);
        if (g is null) return null;
        var (dlat, dlon) = g.Interpolate(lonDeg, latDeg);
        return (dlon / 3600, dlat / 3600);
    }

    /// <summary>Applies the shift (source datum → target datum), radians in/out. Returns false when outside the grid.</summary>
    public bool Forward(ref double lonRad, ref double latRad)
    {
        if (ShiftAt(lonRad / Rad, latRad / Rad) is not { } s) return false;
        lonRad += s.DLon * Rad; latRad += s.DLat * Rad;
        return true;
    }

    /// <summary>Inverse (target → source) by fixed-point iteration, as PROJ does.</summary>
    public bool Inverse(ref double lonRad, ref double latRad)
    {
        double tlon = lonRad / Rad, tlat = latRad / Rad, lon = tlon, lat = tlat;
        for (int i = 0; i < 20; i++)
        {
            if (ShiftAt(lon, lat) is not { } s) return false;
            double nlon = tlon - s.DLon, nlat = tlat - s.DLat;
            bool done = Math.Abs(nlon - lon) < 1e-12 && Math.Abs(nlat - lat) < 1e-12;
            lon = nlon; lat = nlat;
            if (done) break;
        }
        lonRad = lon * Rad; latRad = lat * Rad;
        return true;
    }

    // ================================================================== NTv2 IO

    public static HorizontalGridShift LoadNtv2(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadNtv2(fs, Path.GetFileName(path));
    }

    public static HorizontalGridShift ReadNtv2(Stream stream, string name = "ntv2")
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var b = ms.ToArray();
        if (b.Length < 176 || Encoding.ASCII.GetString(b, 0, 8).Trim() != "NUM_OREC")
            throw new InvalidDataException("Not an NTv2 grid (missing NUM_OREC).");
        bool le = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)) == 11;
        int I(int o) => le ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o));
        double D(int o) => le ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o));
        float F(int o) => le ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o));
        string S(int o) => Encoding.ASCII.GetString(b, o, 8).Trim().TrimEnd('\0');

        int numOrec = I(8), numSrec = I(24), numFile = I(40);
        var overview = new Dictionary<string, int>();
        for (int i = 0; i < numOrec; i++) overview[S(i * 16)] = i * 16 + 8;
        string units = overview.TryGetValue("GS_TYPE", out var gt) ? S(gt) : "SECONDS";
        double unitToSec = units.ToUpperInvariant() switch { "MINUTES" => 60, "DEGREES" => 3600, _ => 1 };
        string? from = overview.TryGetValue("SYSTEM_F", out var sf) ? S(sf) : null;
        string? to = overview.TryGetValue("SYSTEM_T", out var st) ? S(st) : null;

        int pos = numOrec * 16;
        var grids = new List<ShiftSubgrid>();
        for (int g = 0; g < numFile && pos + numSrec * 16 <= b.Length; g++)
        {
            var h = new Dictionary<string, int>();
            for (int i = 0; i < numSrec; i++) h[S(pos + i * 16)] = pos + i * 16 + 8;
            string subName = S(h["SUB_NAME"]);
            string? parent = S(h["PARENT"]);
            double sLat = D(h["S_LAT"]) * unitToSec / 3600, nLat = D(h["N_LAT"]) * unitToSec / 3600;
            double eLongW = D(h["E_LONG"]) * unitToSec / 3600, wLongW = D(h["W_LONG"]) * unitToSec / 3600; // positive west
            double latInc = D(h["LAT_INC"]) * unitToSec / 3600, lonInc = D(h["LONG_INC"]) * unitToSec / 3600;
            int count = I(h["GS_COUNT"]);
            pos += numSrec * 16;
            int rows = (int)Math.Round((nLat - sLat) / latInc) + 1, cols = (int)Math.Round((wLongW - eLongW) / lonInc) + 1;
            if (rows * cols != count) throw new InvalidDataException($"NTv2 subgrid '{subName}': {rows}×{cols} ≠ GS_COUNT {count}.");
            var latS = new float[count]; var lonS = new float[count];
            // file order: south→north rows, each row east→west; store west→east, east-positive shifts
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int o = pos + (r * cols + c) * 16;
                    int dst = r * cols + (cols - 1 - c);
                    latS[dst] = (float)(F(o) * unitToSec);
                    lonS[dst] = (float)(-F(o + 4) * unitToSec);
                }
            pos += count * 16;
            grids.Add(new ShiftSubgrid(subName, parent is "NONE" or "" ? null : parent, sLat, nLat, -wLongW, -eLongW, latInc, lonInc, latS, lonS));
        }
        return new HorizontalGridShift(name, grids, from, to);
    }

    /// <summary>Writes NTv2 (little-endian, seconds) — e.g. to package a locally derived grid.</summary>
    public void WriteNtv2(string path)
    {
        using var fs = File.Create(path);
        var w = new BinaryWriter(fs, Encoding.ASCII);
        void Lbl(string s) => w.Write(Encoding.ASCII.GetBytes(s.PadRight(8)[..8]));
        void RecI(string k, int v) { Lbl(k); w.Write(v); w.Write(0); }
        void RecD(string k, double v) { Lbl(k); w.Write(v); }
        void RecS(string k, string v) { Lbl(k); Lbl(v); }
        RecI("NUM_OREC", 11); RecI("NUM_SREC", 11); RecI("NUM_FILE", Subgrids.Count);
        RecS("GS_TYPE", "SECONDS"); RecS("VERSION", "NTv2.0"); RecS("SYSTEM_F", FromDatum ?? "FROM"); RecS("SYSTEM_T", ToDatum ?? "TO");
        RecD("MAJOR_F", 6378137); RecD("MINOR_F", 6356752.314); RecD("MAJOR_T", 6378137); RecD("MINOR_T", 6356752.314);
        foreach (var g in Subgrids)
        {
            RecS("SUB_NAME", g.Name); RecS("PARENT", g.Parent ?? "NONE"); RecS("CREATED", "20261118"); RecS("UPDATED", "20261118");
            RecD("S_LAT", g.South * 3600); RecD("N_LAT", g.North * 3600); RecD("E_LONG", -g.East * 3600); RecD("W_LONG", -g.West * 3600);
            RecD("LAT_INC", g.LatInc * 3600); RecD("LONG_INC", g.LonInc * 3600); RecI("GS_COUNT", g.Rows * g.Cols);
            for (int r = 0; r < g.Rows; r++)
                for (int c = g.Cols - 1; c >= 0; c--)
                {
                    w.Write(g.LatShift[r * g.Cols + c]); w.Write(-g.LonShift[r * g.Cols + c]); w.Write(0f); w.Write(0f);
                }
        }
        Lbl("END"); w.Write(new byte[8]);
    }

    public override string ToString() => $"NTv2 '{Name}' {FromDatum}→{ToDatum}, {Subgrids.Count} subgrid(s)";
}
