namespace GeoPandas.Net.Raster;

/// <summary>DEM analysis: slope, aspect, hillshade, viewshed, terrain ruggedness (Horn 3×3 kernels).</summary>
public static class Terrain
{
    /// <summary>Slope in degrees (Horn). <paramref name="zFactor"/> converts vertical units to horizontal units.</summary>
    public static Raster Slope(Raster dem, double zFactor = 1)
    {
        var res = new float[dem.Width * dem.Height];
        Kernel(dem, (dzdx, dzdy, i) => res[i] = (float)(Math.Atan(zFactor * Math.Sqrt(dzdx * dzdx + dzdy * dzdy)) * 180 / Math.PI));
        return Named(dem.WithBand(res, float.NaN), "slope");
    }

    /// <summary>Aspect in degrees clockwise from north (0–360); flat cells = -1.</summary>
    public static Raster Aspect(Raster dem)
    {
        var res = new float[dem.Width * dem.Height];
        Kernel(dem, (dzdx, dzdy, i) =>
        {
            if (dzdx == 0 && dzdy == 0) { res[i] = -1; return; }
            double a = Math.Atan2(dzdy, -dzdx) * 180 / Math.PI;
            // convert from math angle to compass bearing
            double aspect = a < 0 ? 90 - a : a > 90 ? 360 - a + 90 : 90 - a;
            res[i] = (float)aspect;
        });
        return Named(dem.WithBand(res, float.NaN), "aspect");
    }

    /// <summary>Analytical hillshade (0–255), sun azimuth/altitude in degrees.</summary>
    public static Raster Hillshade(Raster dem, double azimuth = 315, double altitude = 45, double zFactor = 1)
    {
        double zenith = (90 - altitude) * Math.PI / 180;
        double az = (360 - azimuth + 90) % 360 * Math.PI / 180;
        var res = new float[dem.Width * dem.Height];
        Kernel(dem, (dzdx, dzdy, i) =>
        {
            double slope = Math.Atan(zFactor * Math.Sqrt(dzdx * dzdx + dzdy * dzdy));
            double aspect = dzdx != 0 ? Math.Atan2(dzdy, -dzdx) : dzdy > 0 ? Math.PI / 2 : dzdy < 0 ? 3 * Math.PI / 2 : 0;
            if (aspect < 0) aspect += 2 * Math.PI;
            double hs = 255 * (Math.Cos(zenith) * Math.Cos(slope) + Math.Sin(zenith) * Math.Sin(slope) * Math.Cos(az - aspect));
            res[i] = (float)Math.Max(0, hs);
        });
        return Named(dem.WithBand(res, float.NaN), "hillshade");
    }

    /// <summary>Terrain Ruggedness Index (Riley): mean absolute elevation difference to the 8 neighbours.</summary>
    public static Raster Ruggedness(Raster dem)
    {
        int w = dem.Width, h = dem.Height;
        var z = dem.Bands[0];
        var res = new float[w * h];
        Parallel.For(0, h, r =>
        {
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                if (dem.IsNoData(z[i])) { res[i] = float.NaN; continue; }
                double s = 0; int n = 0;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= h || cc >= w || dem.IsNoData(z[rr * w + cc])) continue;
                        s += Math.Abs(z[rr * w + cc] - z[i]); n++;
                    }
                res[i] = n == 0 ? float.NaN : (float)(s / n);
            }
        });
        return Named(dem.WithBand(res, float.NaN), "tri");
    }

    /// <summary>
    /// Binary viewshed from an observer location (world coords) with observer/target heights.
    /// Each target cell is tested by marching along the line of sight (Bresenham-like sampling).
    /// </summary>
    public static Raster Viewshed(Raster dem, double observerX, double observerY, double observerHeight = 1.7, double targetHeight = 0, double maxDistance = double.PositiveInfinity)
    {
        int w = dem.Width, h = dem.Height;
        var z = dem.Bands[0];
        var (oc, or) = dem.Transform.WorldToPixel(observerX, observerY);
        int c0 = (int)oc, r0 = (int)or;
        if (c0 < 0 || r0 < 0 || c0 >= w || r0 >= h) throw new ArgumentException("Observer is outside the raster.");
        double oz = z[r0 * w + c0] + observerHeight;
        double cs = dem.CellSizeX;
        var res = new float[w * h];
        Parallel.For(0, h, r =>
        {
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                if (dem.IsNoData(z[i])) { res[i] = float.NaN; continue; }
                double dc = c - c0, dr = r - r0;
                double distCells = Math.Sqrt(dc * dc + dr * dr);
                if (distCells * cs > maxDistance) { res[i] = 0; continue; }
                if (distCells < 1) { res[i] = 1; continue; }
                double targetSlope = (z[i] + targetHeight - oz) / distCells;
                int steps = (int)Math.Ceiling(distCells);
                bool visible = true;
                for (int s = 1; s < steps; s++)
                {
                    double t = s / (double)steps;
                    int cc = (int)Math.Round(c0 + dc * t), rr = (int)Math.Round(r0 + dr * t);
                    double zz = z[rr * w + cc];
                    if ((zz - oz) / (distCells * t) > targetSlope) { visible = false; break; }
                }
                res[i] = visible ? 1 : 0;
            }
        });
        return Named(dem.WithBand(res, float.NaN), "viewshed");
    }

    private static Raster Named(Raster r, string name) { r.BandNames = [name]; return r; }

    /// <summary>Applies Horn's 3×3 finite-difference to every interior cell (edges replicate).</summary>
    private static void Kernel(Raster dem, Action<double, double, int> emit)
    {
        int w = dem.Width, h = dem.Height;
        var z = dem.Bands[0];
        double cx = dem.CellSizeX, cy = dem.CellSizeY;
        if (dem.Crs?.IsGeographic == true)
        {
            // approximate metres per degree at the raster centre
            double lat = dem.Bounds.Centre.Y * Math.PI / 180;
            cx *= 111_320 * Math.Cos(lat);
            cy *= 110_540;
        }
        Parallel.For(0, h, r =>
        {
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                if (dem.IsNoData(z[i])) { emit(double.NaN, double.NaN, i); continue; }
                double Z(int dr, int dc)
                {
                    int rr = Math.Clamp(r + dr, 0, h - 1), cc = Math.Clamp(c + dc, 0, w - 1);
                    var v = z[rr * w + cc];
                    return dem.IsNoData(v) ? z[i] : v;
                }
                double a = Z(-1, -1), b = Z(-1, 0), cc2 = Z(-1, 1), d = Z(0, -1), f = Z(0, 1), g = Z(1, -1), hh = Z(1, 0), ii = Z(1, 1);
                double dzdx = ((cc2 + 2 * f + ii) - (a + 2 * d + g)) / (8 * cx);
                double dzdy = ((g + 2 * hh + ii) - (a + 2 * b + cc2)) / (8 * cy);
                emit(dzdx, dzdy, i);
            }
        });
    }
}

/// <summary>Hydrology for watershed analysis (civil engineering): sink filling, D8 flow, accumulation, watersheds, streams.</summary>
public static class Hydrology
{
    private static readonly int[] DR = [-1, -1, 0, 1, 1, 1, 0, -1];
    private static readonly int[] DC = [0, 1, 1, 1, 0, -1, -1, -1];
    /// <summary>ESRI D8 codes for directions N, NE, E, SE, S, SW, W, NW.</summary>
    private static readonly int[] Codes = [64, 128, 1, 2, 4, 8, 16, 32];

    /// <summary>Priority-flood depression filling (Barnes et al. 2014) with a small epsilon gradient.</summary>
    public static Raster FillSinks(Raster dem, float epsilon = 1e-4f)
    {
        int w = dem.Width, h = dem.Height;
        var z = (float[])dem.Bands[0].Clone();
        var closed = new bool[w * h];
        var pq = new PriorityQueue<int, float>();
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                if (r == 0 || c == 0 || r == h - 1 || c == w - 1)
                {
                    int i = r * w + c;
                    closed[i] = true;
                    if (!dem.IsNoData(z[i])) pq.Enqueue(i, z[i]);
                }
        while (pq.TryDequeue(out int i, out float zi))
        {
            int r = i / w, c = i % w;
            for (int k = 0; k < 8; k++)
            {
                int rr = r + DR[k], cc = c + DC[k];
                if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                int j = rr * w + cc;
                if (closed[j]) continue;
                closed[j] = true;
                if (dem.IsNoData(z[j])) continue;
                if (z[j] <= zi) z[j] = zi + epsilon;
                pq.Enqueue(j, z[j]);
            }
        }
        var res = dem.WithBand(z);
        res.BandNames = ["filled"];
        return res;
    }

    /// <summary>D8 flow direction (ESRI codes 1,2,4,…,128; 0 = pit/edge).</summary>
    public static Raster FlowDirection(Raster dem)
    {
        int w = dem.Width, h = dem.Height;
        var z = dem.Bands[0];
        var res = new float[w * h];
        double cs = dem.CellSizeX;
        Parallel.For(0, h, r =>
        {
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                if (dem.IsNoData(z[i])) { res[i] = float.NaN; continue; }
                double best = 0; int code = 0;
                for (int k = 0; k < 8; k++)
                {
                    int rr = r + DR[k], cc = c + DC[k];
                    if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                    var zj = z[rr * w + cc];
                    if (dem.IsNoData(zj)) continue;
                    double drop = (z[i] - zj) / (k % 2 == 1 ? cs * Math.Sqrt(2) : cs);
                    if (drop > best) { best = drop; code = Codes[k]; }
                }
                res[i] = code;
            }
        });
        var o = dem.WithBand(res, float.NaN);
        o.BandNames = ["flowdir"];
        return o;
    }

    /// <summary>Flow accumulation (number of upstream cells) from a D8 direction raster.</summary>
    public static Raster FlowAccumulation(Raster flowDir)
    {
        int w = flowDir.Width, h = flowDir.Height;
        var fd = flowDir.Bands[0];
        var down = new int[w * h];
        var indeg = new int[w * h];
        for (int i = 0; i < down.Length; i++)
        {
            down[i] = Downstream(fd[i], i, w, h);
            if (down[i] >= 0) indeg[down[i]]++;
        }
        var acc = new float[w * h];
        var q = new Queue<int>();
        for (int i = 0; i < acc.Length; i++) { acc[i] = float.IsNaN(fd[i]) ? float.NaN : 1; if (indeg[i] == 0) q.Enqueue(i); }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            int j = down[i];
            if (j < 0) continue;
            if (!float.IsNaN(acc[i])) acc[j] += acc[i];
            if (--indeg[j] == 0) q.Enqueue(j);
        }
        var o = flowDir.WithBand(acc, float.NaN);
        o.BandNames = ["flowacc"];
        return o;
    }

    private static int Downstream(float code, int i, int w, int h)
    {
        if (float.IsNaN(code) || code == 0) return -1;
        int k = Array.IndexOf(Codes, (int)code);
        if (k < 0) return -1;
        int r = i / w + DR[k], c = i % w + DC[k];
        return r < 0 || c < 0 || r >= h || c >= w ? -1 : r * w + c;
    }

    /// <summary>Watershed (catchment) draining to a pour point (world coordinates). 1 = inside.</summary>
    public static Raster Watershed(Raster flowDir, double pourX, double pourY)
    {
        int w = flowDir.Width, h = flowDir.Height;
        var fd = flowDir.Bands[0];
        var (pc, pr) = flowDir.Transform.WorldToPixel(pourX, pourY);
        int start = (int)pr * w + (int)pc;
        var upstream = new List<int>[w * h];
        for (int i = 0; i < fd.Length; i++)
        {
            int d = Downstream(fd[i], i, w, h);
            if (d >= 0) (upstream[d] ??= []).Add(i);
        }
        var res = new float[w * h];
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (res[i] == 1) continue;
            res[i] = 1;
            if (upstream[i] is { } ups) foreach (var u in ups) stack.Push(u);
        }
        var o = flowDir.WithBand(res, 0);
        o.BandNames = ["watershed"];
        return o;
    }

    /// <summary>Stream cells where accumulation ≥ threshold (1 = stream).</summary>
    public static Raster Streams(Raster accumulation, float threshold) =>
        accumulation.Map(v => v >= threshold ? 1 : 0);
}

/// <summary>Remote-sensing spectral indices and change detection.</summary>
public static class SpectralIndex
{
    private static Raster Normalized(Raster a, Raster b, string name)
    {
        var r = Raster.Combine(a, b, (x, y) => x + y == 0 ? float.NaN : (x - y) / (x + y));
        r.BandNames = [name];
        return r;
    }

    /// <summary>NDVI = (NIR − Red) / (NIR + Red). Sentinel-2: B8, B4. Landsat 8/9: B5, B4.</summary>
    public static Raster Ndvi(Raster nir, Raster red) => Normalized(nir, red, "ndvi");
    /// <summary>NDWI (McFeeters) = (Green − NIR) / (Green + NIR).</summary>
    public static Raster Ndwi(Raster green, Raster nir) => Normalized(green, nir, "ndwi");
    /// <summary>NDBI = (SWIR − NIR) / (SWIR + NIR) — built-up areas.</summary>
    public static Raster Ndbi(Raster swir, Raster nir) => Normalized(swir, nir, "ndbi");
    /// <summary>Soil-adjusted vegetation index.</summary>
    public static Raster Savi(Raster nir, Raster red, float l = 0.5f)
    {
        var r = Raster.Combine(nir, red, (n, rd) => (1 + l) * (n - rd) / (n + rd + l));
        r.BandNames = ["savi"];
        return r;
    }

    /// <summary>Band of a multi-band raster as its own raster.</summary>
    public static Raster Band(Raster multi, int index) => new(multi.Width, multi.Height, [multi.Bands[index]], multi.Transform, multi.Crs, multi.NoData);

    /// <summary>
    /// Change detection by differencing: returns (difference, change mask) where |after − before| ≥ threshold.
    /// The default threshold is chosen with Otsu's method on |difference| (robust for bimodal change maps).
    /// Mask values: 1 = increase, -1 = decrease, 0 = no change.
    /// </summary>
    public static (Raster Difference, Raster Change) ChangeDetection(Raster before, Raster after, double? threshold = null)
    {
        var diff = Raster.Combine(after, before, (a, b) => a - b);
        diff.BandNames = ["difference"];
        double t = threshold ?? Otsu(diff.Map(Math.Abs).Bands[0]);
        var change = diff.Map(v => Math.Abs(v) >= t ? (v > 0 ? 1 : -1) : 0);
        change.BandNames = ["change"];
        return (diff, change);
    }

    /// <summary>Otsu's threshold (maximum between-class variance) over a 256-bin histogram; NaNs ignored.</summary>
    public static double Otsu(ReadOnlySpan<float> values)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in values) if (!float.IsNaN(v)) { if (v < min) min = v; if (v > max) max = v; }
        if (min >= max) return max;
        var hist = new long[256];
        long total = 0;
        foreach (var v in values)
        {
            if (float.IsNaN(v)) continue;
            hist[Math.Min(255, (int)((v - min) / (max - min) * 256))]++;
            total++;
        }
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];
        double sumB = 0, best = -1; long wB = 0; int bestIdx = 0;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += i * (double)hist[i];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; bestIdx = i; }
        }
        return min + (bestIdx + 1) * (max - min) / 256.0;
    }
}
