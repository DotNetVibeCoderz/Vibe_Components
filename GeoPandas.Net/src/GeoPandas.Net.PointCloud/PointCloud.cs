using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Index;
using GeoPandas.Net.Raster;
using NetTopologySuite.Geometries;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.PointCloud;

/// <summary>ASPRS LAS standard classification codes.</summary>
public static class LasClass
{
    public const byte NeverClassified = 0, Unclassified = 1, Ground = 2, LowVegetation = 3, MediumVegetation = 4, HighVegetation = 5,
        Building = 6, LowPoint = 7, Water = 9, Rail = 10, RoadSurface = 11, WireGuard = 13, WireConductor = 14, TransmissionTower = 15,
        WireConnector = 16, BridgeDeck = 17, HighNoise = 18;

    public static string Name(byte code) => code switch
    {
        0 => "never classified", 1 => "unclassified", 2 => "ground", 3 => "low vegetation", 4 => "medium vegetation", 5 => "high vegetation",
        6 => "building", 7 => "low point (noise)", 8 => "reserved", 9 => "water", 10 => "rail", 11 => "road surface", 12 => "overlap",
        13 => "wire – guard", 14 => "wire – conductor", 15 => "transmission tower", 16 => "wire connector", 17 => "bridge deck", 18 => "high noise",
        _ => $"class {code}"
    };
}

/// <summary>How <see cref="PointCloud.ToRaster"/> aggregates the points that fall into one cell.</summary>
public enum CellStatistic { Max, Min, Mean, Count, Range }

/// <summary>
/// A LiDAR / photogrammetry point cloud in columnar (structure-of-arrays) form: coordinates, intensity, returns, classification,
/// GPS time and colour. Indexable with an <see cref="Octree"/>, filterable, rasterisable to DSM/DTM/CHM and convertible to a GeoDataFrame.
/// </summary>
public sealed class PointCloud
{
    private Octree? _octree;

    public PointCloud(double[] x, double[] y, double[] z, CoordinateReferenceSystem? crs = null)
    {
        if (x.Length != y.Length || y.Length != z.Length) throw new ArgumentException("x, y and z must have the same length.");
        X = x; Y = y; Z = z; Crs = crs;
        int n = x.Length;
        Intensity = new ushort[n]; Classification = new byte[n]; ReturnNumber = new byte[n]; NumberOfReturns = new byte[n];
        PointSourceId = new ushort[n]; UserData = new byte[n]; ScanAngle = new float[n];
        Array.Fill(ReturnNumber, (byte)1); Array.Fill(NumberOfReturns, (byte)1);
    }

    public int Count => X.Length;
    public double[] X { get; }
    public double[] Y { get; }
    public double[] Z { get; }
    public ushort[] Intensity { get; init; }
    public byte[] Classification { get; init; }
    public byte[] ReturnNumber { get; init; }
    public byte[] NumberOfReturns { get; init; }
    public ushort[] PointSourceId { get; init; }
    public byte[] UserData { get; init; }
    /// <summary>Scan angle in degrees.</summary>
    public float[] ScanAngle { get; init; }
    public double[]? GpsTime { get; init; }
    public ushort[]? Red { get; init; }
    public ushort[]? Green { get; init; }
    public ushort[]? Blue { get; init; }
    public ushort[]? Nir { get; init; }
    public CoordinateReferenceSystem? Crs { get; init; }
    /// <summary>Header of the file the cloud was read from (null for clouds built in memory).</summary>
    public LasInfo? Source { get; init; }

    public bool HasColor => Red is not null && Green is not null && Blue is not null;

    /// <summary>3-D bounds (minX, minY, minZ, maxX, maxY, maxZ).</summary>
    public (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            if (Count == 0) return (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
            double a = double.MaxValue, b = double.MaxValue, c = double.MaxValue, d = double.MinValue, e = double.MinValue, f = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                a = Math.Min(a, X[i]); b = Math.Min(b, Y[i]); c = Math.Min(c, Z[i]);
                d = Math.Max(d, X[i]); e = Math.Max(e, Y[i]); f = Math.Max(f, Z[i]);
            }
            return (a, b, c, d, e, f);
        }
    }

    public Envelope Envelope2D { get { var b = Bounds; return Count == 0 ? new Envelope() : new Envelope(b.MinX, b.MaxX, b.MinY, b.MaxY); } }

    /// <summary>Octree over the points (built once, thread-safe).</summary>
    public Octree Octree => LazyInitializer.EnsureInitialized(ref _octree, () => new Octree(X, Y, Z));

    // ------------------------------------------------------------------ selection

    /// <summary>Subset by point indices (all attributes follow).</summary>
    public PointCloud Take(int[] idx)
    {
        T[] G<T>(T[] a) { var r = new T[idx.Length]; for (int i = 0; i < idx.Length; i++) r[i] = a[idx[i]]; return r; }
        T[]? O<T>(T[]? a) => a is null ? null : G(a);
        return new PointCloud(G(X), G(Y), G(Z), Crs)
        {
            Intensity = G(Intensity), Classification = G(Classification), ReturnNumber = G(ReturnNumber), NumberOfReturns = G(NumberOfReturns),
            PointSourceId = G(PointSourceId), UserData = G(UserData), ScanAngle = G(ScanAngle),
            GpsTime = O(GpsTime), Red = O(Red), Green = O(Green), Blue = O(Blue), Nir = O(Nir), Source = Source
        };
    }

    public PointCloud Filter(Func<int, bool> predicate) => Take(Enumerable.Range(0, Count).Where(predicate).ToArray());

    /// <summary>Points of the given ASPRS classes (e.g. <c>ByClass(LasClass.Ground)</c>).</summary>
    public PointCloud ByClass(params byte[] classes)
    {
        var set = new bool[256];
        foreach (var c in classes) set[c] = true;
        return Filter(i => set[Classification[i]]);
    }

    /// <summary>First returns only (canopy / roof surface).</summary>
    public PointCloud FirstReturns() => Filter(i => ReturnNumber[i] == 1);
    /// <summary>Last returns only (closest to the ground).</summary>
    public PointCloud LastReturns() => Filter(i => ReturnNumber[i] == NumberOfReturns[i]);

    /// <summary>Points inside a 2-D box (uses the octree).</summary>
    public PointCloud Crop(Envelope box)
    {
        var b = Bounds;
        var idx = Octree.QueryBox(box.MinX, box.MinY, b.MinZ, box.MaxX, box.MaxY, b.MaxZ);
        idx.Sort();
        return Take(idx.ToArray());
    }

    /// <summary>Points inside a polygon.</summary>
    public PointCloud Clip(NetTopologySuite.Geometries.Geometry polygon)
    {
        var prepared = NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(polygon);
        var box = Crop(polygon.EnvelopeInternal);
        return box.Filter(i => prepared.Covers(Geo.Point(box.X[i], box.Y[i])));
    }

    /// <summary>Indices of the points within <paramref name="radius"/> of a 3-D location.</summary>
    public List<int> Radius(double x, double y, double z, double radius) => Octree.QueryRadius(x, y, z, radius);

    /// <summary>Voxel-grid thinning: keeps the point closest to each voxel centre.</summary>
    public PointCloud Thin(double voxelSize)
    {
        var b = Bounds;
        var best = new Dictionary<(long, long, long), (int Index, double D2)>();
        for (int i = 0; i < Count; i++)
        {
            long ix = (long)Math.Floor((X[i] - b.MinX) / voxelSize), iy = (long)Math.Floor((Y[i] - b.MinY) / voxelSize), iz = (long)Math.Floor((Z[i] - b.MinZ) / voxelSize);
            double cx = b.MinX + (ix + 0.5) * voxelSize, cy = b.MinY + (iy + 0.5) * voxelSize, cz = b.MinZ + (iz + 0.5) * voxelSize;
            double d2 = (X[i] - cx) * (X[i] - cx) + (Y[i] - cy) * (Y[i] - cy) + (Z[i] - cz) * (Z[i] - cz);
            var key = (ix, iy, iz);
            if (!best.TryGetValue(key, out var cur) || d2 < cur.D2) best[key] = (i, d2);
        }
        return Take(best.Values.Select(v => v.Index).Order().ToArray());
    }

    /// <summary>Point counts per ASPRS class.</summary>
    public IReadOnlyList<(byte Class, string Name, int Count)> ClassCounts()
    {
        var c = new int[256];
        foreach (var k in Classification) c[k]++;
        return Enumerable.Range(0, 256).Where(i => c[i] > 0).Select(i => ((byte)i, LasClass.Name((byte)i), c[i])).ToArray();
    }

    // ------------------------------------------------------------------ rasterisation

    /// <summary>
    /// Grids the points: <see cref="CellStatistic.Max"/> of all points is a DSM, <see cref="CellStatistic.Min"/>/<see cref="CellStatistic.Mean"/>
    /// of ground points a DTM. Empty cells are NaN unless <paramref name="fillGaps"/> &gt; 0 (iterative neighbour averaging).
    /// </summary>
    public RasterGrid ToRaster(double cellSize, CellStatistic statistic = CellStatistic.Max, int fillGaps = 0)
    {
        if (Count == 0) throw new InvalidOperationException("Point cloud is empty.");
        var b = Bounds;
        double minX = Math.Floor(b.MinX / cellSize) * cellSize, maxY = Math.Ceiling(b.MaxY / cellSize) * cellSize;
        int w = Math.Max(1, (int)Math.Ceiling((b.MaxX - minX) / cellSize + 1e-9));
        int h = Math.Max(1, (int)Math.Ceiling((maxY - b.MinY) / cellSize + 1e-9));
        var acc = new double[w * h];
        var acc2 = new double[w * h];
        var cnt = new int[w * h];
        Array.Fill(acc, statistic == CellStatistic.Min ? double.MaxValue : statistic is CellStatistic.Max or CellStatistic.Range ? double.MinValue : 0);
        Array.Fill(acc2, double.MaxValue);
        for (int i = 0; i < Count; i++)
        {
            int c = Math.Clamp((int)((X[i] - minX) / cellSize), 0, w - 1);
            int r = Math.Clamp((int)((maxY - Y[i]) / cellSize), 0, h - 1);
            int k = r * w + c;
            cnt[k]++;
            switch (statistic)
            {
                case CellStatistic.Max: acc[k] = Math.Max(acc[k], Z[i]); break;
                case CellStatistic.Min: acc[k] = Math.Min(acc[k], Z[i]); break;
                case CellStatistic.Mean: acc[k] += Z[i]; break;
                case CellStatistic.Range: acc[k] = Math.Max(acc[k], Z[i]); acc2[k] = Math.Min(acc2[k], Z[i]); break;
            }
        }
        var band = new float[w * h];
        for (int k = 0; k < band.Length; k++)
            band[k] = statistic == CellStatistic.Count ? cnt[k]
                : cnt[k] == 0 ? float.NaN
                : statistic switch { CellStatistic.Mean => (float)(acc[k] / cnt[k]), CellStatistic.Range => (float)(acc[k] - acc2[k]), _ => (float)acc[k] };
        for (int pass = 0; pass < fillGaps; pass++)
        {
            var next = (float[])band.Clone();
            bool any = false;
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    if (!float.IsNaN(band[r * w + c])) continue;
                    double s = 0; int n = 0;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            int rr = r + dr, cc = c + dc;
                            if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                            float v = band[rr * w + cc];
                            if (!float.IsNaN(v)) { s += v; n++; }
                        }
                    if (n > 0) { next[r * w + c] = (float)(s / n); any = true; }
                }
            band = next;
            if (!any) break;
        }
        return new RasterGrid(w, h, [band], GeoTransform.NorthUp(minX, maxY, cellSize), Crs, float.NaN)
        {
            BandNames = [statistic.ToString().ToLowerInvariant()]
        };
    }

    /// <summary>Digital surface model (highest point per cell).</summary>
    public RasterGrid Dsm(double cellSize, int fillGaps = 2) => ToRaster(cellSize, CellStatistic.Max, fillGaps);

    /// <summary>Digital terrain model from ground-classified points (class 2), gaps filled.</summary>
    public RasterGrid Dtm(double cellSize, int fillGaps = 8)
    {
        var ground = ByClass(LasClass.Ground);
        if (ground.Count == 0) throw new InvalidOperationException("No ground-classified (class 2) points; classify the cloud first.");
        return ground.ToRaster(cellSize, CellStatistic.Mean, fillGaps);
    }

    /// <summary>Copy with Z replaced by height above the terrain model (bilinear DTM sample).</summary>
    public PointCloud HeightAboveGround(RasterGrid dtm)
    {
        var z = new double[Count];
        for (int i = 0; i < Count; i++)
        {
            float g = dtm.SampleBilinear(X[i], Y[i]);
            z[i] = float.IsNaN(g) ? double.NaN : Z[i] - g;
        }
        return new PointCloud(X, Y, z, Crs)
        {
            Intensity = Intensity, Classification = Classification, ReturnNumber = ReturnNumber, NumberOfReturns = NumberOfReturns,
            PointSourceId = PointSourceId, UserData = UserData, ScanAngle = ScanAngle, GpsTime = GpsTime, Red = Red, Green = Green, Blue = Blue, Nir = Nir, Source = Source
        };
    }

    // ------------------------------------------------------------------ GeoDataFrame interop

    /// <summary>Points as a GeoDataFrame (POINT Z plus attribute columns). Use <see cref="Thin"/> or <see cref="Crop"/> first for large clouds.</summary>
    public GeoDataFrame ToGeoDataFrame()
    {
        var f = Geo.Factory;
        var pts = new NetTopologySuite.Geometries.Geometry?[Count];
        for (int i = 0; i < Count; i++) pts[i] = f.CreatePoint(new CoordinateZ(X[i], Y[i], Z[i]));
        var cols = new List<Column>
        {
            Column.Create("z", Z), Column.Create("intensity", Intensity.Select(v => (long)v).ToArray()),
            Column.Create("classification", Classification.Select(v => (long)v).ToArray()),
            Column.Create("return_number", ReturnNumber.Select(v => (long)v).ToArray()),
            Column.Create("number_of_returns", NumberOfReturns.Select(v => (long)v).ToArray()),
        };
        if (GpsTime is not null) cols.Add(Column.Create("gps_time", GpsTime));
        if (HasColor)
        {
            cols.Add(Column.Create("red", Red!.Select(v => (long)v).ToArray()));
            cols.Add(Column.Create("green", Green!.Select(v => (long)v).ToArray()));
            cols.Add(Column.Create("blue", Blue!.Select(v => (long)v).ToArray()));
        }
        cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, pts, Crs));
        return new GeoDataFrame(cols);
    }

    /// <summary>Builds a cloud from point geometries (Z from the geometry, or from <paramref name="zColumn"/>).</summary>
    public static PointCloud FromGeoDataFrame(GeoDataFrame gdf, string? zColumn = null, string? classColumn = null)
    {
        int n = gdf.RowCount;
        var x = new double[n]; var y = new double[n]; var z = new double[n];
        for (int i = 0; i < n; i++)
        {
            var c = gdf.Geometry[i]?.Coordinate ?? throw new ArgumentException($"Row {i} has no geometry.");
            x[i] = c.X; y[i] = c.Y;
            z[i] = zColumn is not null ? gdf[zColumn].GetDouble(i) : double.IsNaN(c.Z) ? 0 : c.Z;
        }
        var pc = new PointCloud(x, y, z, gdf.Crs);
        if (classColumn is not null) for (int i = 0; i < n; i++) pc.Classification[i] = (byte)gdf[classColumn].GetDouble(i);
        return pc;
    }

    public override string ToString()
    {
        var b = Bounds;
        return $"PointCloud {Count:N0} points, x [{b.MinX:F2}, {b.MaxX:F2}] y [{b.MinY:F2}, {b.MaxY:F2}] z [{b.MinZ:F2}, {b.MaxZ:F2}], crs={Crs?.ToString() ?? "None"}";
    }
}
