using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Gpu;

/// <summary>An ILGPU context and accelerator (CUDA, then OpenCL, then the multi-threaded CPU accelerator).</summary>
public sealed class GpuContext : IDisposable
{
    private readonly Context _context;

    private GpuContext(Context context, Accelerator accelerator) { _context = context; Accelerator = accelerator; }

    public Accelerator Accelerator { get; }
    public AcceleratorType Type => Accelerator.AcceleratorType;
    public string Name => Accelerator.Name;

    /// <summary>Creates a context on the best available device; <paramref name="preferGpu"/> = false forces the CPU accelerator.</summary>
    public static GpuContext Create(bool preferGpu = true)
    {
        var context = Context.Create(b => b.Default().EnableAlgorithms());
        Accelerator? acc = null;
        if (preferGpu)
        {
            var cuda = context.GetCudaDevices();
            if (cuda.Count > 0) acc = cuda[0].CreateAccelerator(context);
            else
            {
                foreach (var device in context.GetCLDevices())
                {
                    acc = device.CreateAccelerator(context);
                    break;
                }
            }
        }
        acc ??= context.GetCPUDevice(0).CreateAccelerator(context);
        return new GpuContext(context, acc);
    }

    public void Dispose()
    {
        Accelerator.Dispose();
        _context.Dispose();
    }

    public override string ToString() => $"{Type}: {Name}";
}

/// <summary>
/// Raster operations as ILGPU kernels: Horn slope / aspect / hillshade (identical to <see cref="GeoPandas.Net.Raster.Terrain"/>), normalised
/// differences (NDVI, NDWI…), 2-D convolution and IDW interpolation. Runs on CUDA / OpenCL GPUs or the CPU accelerator.
/// </summary>
public static class GpuRaster
{
    private enum TerrainOp { Slope = 0, Aspect = 1, Hillshade = 2 }

    // ILGPU kernels cannot use closures: neighbour access is a static helper (edges clamp, no-data falls back to the centre, as Terrain)
    private static double Cell(ArrayView2D<float, Stride2D.DenseX> z, int r, int c, int w, int h, float centre, float noData, int hasNoData)
    {
        int rr = r < 0 ? 0 : r >= h ? h - 1 : r, cc = c < 0 ? 0 : c >= w ? w - 1 : c;
        float v = z[cc, rr];
        return float.IsNaN(v) || (hasNoData != 0 && v == noData) ? centre : v;
    }

    private static void TerrainKernel(Index2D idx, ArrayView2D<float, Stride2D.DenseX> z, ArrayView2D<float, Stride2D.DenseX> output,
        float noData, int hasNoData, double cx, double cy, int op, double zFactor, double zenith, double az)
    {
        int c = idx.X, r = idx.Y;
        int w = (int)z.IntExtent.X, h = (int)z.IntExtent.Y;
        float centre = z[c, r];
        if (float.IsNaN(centre) || (hasNoData != 0 && centre == noData)) { output[c, r] = float.NaN; return; }
        double a = Cell(z, r - 1, c - 1, w, h, centre, noData, hasNoData), b = Cell(z, r - 1, c, w, h, centre, noData, hasNoData),
            c2 = Cell(z, r - 1, c + 1, w, h, centre, noData, hasNoData), d = Cell(z, r, c - 1, w, h, centre, noData, hasNoData),
            f = Cell(z, r, c + 1, w, h, centre, noData, hasNoData), g = Cell(z, r + 1, c - 1, w, h, centre, noData, hasNoData),
            hh = Cell(z, r + 1, c, w, h, centre, noData, hasNoData), ii = Cell(z, r + 1, c + 1, w, h, centre, noData, hasNoData);
        double dzdx = ((c2 + 2 * f + ii) - (a + 2 * d + g)) / (8 * cx);
        double dzdy = ((g + 2 * hh + ii) - (a + 2 * b + c2)) / (8 * cy);
        if (op == 0)
        {
            output[c, r] = (float)(Math.Atan(zFactor * Math.Sqrt(dzdx * dzdx + dzdy * dzdy)) * 180 / Math.PI);
        }
        else if (op == 1)
        {
            if (dzdx == 0 && dzdy == 0) { output[c, r] = -1; return; }
            double an = Math.Atan2(dzdy, -dzdx) * 180 / Math.PI;
            output[c, r] = (float)(an < 0 ? 90 - an : an > 90 ? 360 - an + 90 : 90 - an);
        }
        else
        {
            double slope = Math.Atan(zFactor * Math.Sqrt(dzdx * dzdx + dzdy * dzdy));
            double aspect = dzdx != 0 ? Math.Atan2(dzdy, -dzdx) : dzdy > 0 ? Math.PI / 2 : dzdy < 0 ? 3 * Math.PI / 2 : 0;
            if (aspect < 0) aspect += 2 * Math.PI;
            double hs = 255 * (Math.Cos(zenith) * Math.Cos(slope) + Math.Sin(zenith) * Math.Sin(slope) * Math.Cos(az - aspect));
            output[c, r] = (float)(hs > 0 ? hs : 0);
        }
    }

    private static RasterGrid RunTerrain(GpuContext gpu, RasterGrid dem, TerrainOp op, double zFactor = 1, double azimuth = 315, double altitude = 45)
    {
        double cx = dem.CellSizeX, cy = dem.CellSizeY;
        if (dem.Crs?.IsGeographic == true)
        {
            double lat = dem.Bounds.Centre.Y * Math.PI / 180;
            cx *= 111_320 * Math.Cos(lat);
            cy *= 110_540;
        }
        var acc = gpu.Accelerator;
        var kernel = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView2D<float, Stride2D.DenseX>, ArrayView2D<float, Stride2D.DenseX>, float, int, double, double, int, double, double, double>(TerrainKernel);
        var extent = new LongIndex2D(dem.Width, dem.Height);
        using var input = acc.Allocate2DDenseX<float>(extent);
        using var output = acc.Allocate2DDenseX<float>(extent);
        input.View.BaseView.CopyFromCPU(dem.Bands[0]);
        double zenith = (90 - altitude) * Math.PI / 180, az = (360 - azimuth + 90) % 360 * Math.PI / 180;
        kernel(new Index2D(dem.Width, dem.Height), input.View, output.View, dem.NoData ?? float.NaN, dem.NoData is null ? 0 : 1, cx, cy, (int)op, zFactor, zenith, az);
        acc.Synchronize();
        var result = output.View.BaseView.GetAsArray();
        var r = dem.WithBand(result, float.NaN);
        r.BandNames = [op.ToString().ToLowerInvariant()];
        return r;
    }

    /// <summary>Slope in degrees (Horn), as <c>Terrain.Slope</c>.</summary>
    public static RasterGrid Slope(GpuContext gpu, RasterGrid dem, double zFactor = 1) => RunTerrain(gpu, dem, TerrainOp.Slope, zFactor);

    /// <summary>Aspect in degrees clockwise from north (flat = −1), as <c>Terrain.Aspect</c>.</summary>
    public static RasterGrid Aspect(GpuContext gpu, RasterGrid dem) => RunTerrain(gpu, dem, TerrainOp.Aspect);

    /// <summary>Analytical hillshade 0–255, as <c>Terrain.Hillshade</c>.</summary>
    public static RasterGrid Hillshade(GpuContext gpu, RasterGrid dem, double azimuth = 315, double altitude = 45, double zFactor = 1) =>
        RunTerrain(gpu, dem, TerrainOp.Hillshade, zFactor, azimuth, altitude);

    private static void NormDiffKernel(Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> output)
    {
        float x = a[i], y = b[i], s = x + y;
        output[i] = float.IsNaN(x) || float.IsNaN(y) || s == 0 ? float.NaN : (x - y) / s;
    }

    /// <summary>(a − b) / (a + b) of two bands (NDVI = (NIR − red) / (NIR + red)).</summary>
    public static RasterGrid NormalizedDifference(GpuContext gpu, RasterGrid raster, int bandA, int bandB)
    {
        var acc = gpu.Accelerator;
        var kernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>>(NormDiffKernel);
        int n = raster.Width * raster.Height;
        using var a = acc.Allocate1D(raster.Bands[bandA]);
        using var b = acc.Allocate1D(raster.Bands[bandB]);
        using var o = acc.Allocate1D<float>(n);
        kernel(n, a.View, b.View, o.View);
        acc.Synchronize();
        var r = raster.WithBand(o.GetAsArray1D(), float.NaN);
        r.BandNames = ["normalized_difference"];
        return r;
    }

    private static void ConvolveKernel(Index2D idx, ArrayView2D<float, Stride2D.DenseX> z, ArrayView2D<float, Stride2D.DenseX> k, ArrayView2D<float, Stride2D.DenseX> output)
    {
        int c = idx.X, r = idx.Y, w = (int)z.IntExtent.X, h = (int)z.IntExtent.Y, kw = (int)k.IntExtent.X, kh = (int)k.IntExtent.Y;
        double sum = 0, weight = 0;
        for (int dy = 0; dy < kh; dy++)
            for (int dx = 0; dx < kw; dx++)
            {
                int rr = r + dy - kh / 2, cc = c + dx - kw / 2;
                if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                float v = z[cc, rr];
                if (float.IsNaN(v)) continue;
                float kv = k[dx, dy];
                sum += kv * v; weight += kv;
            }
        output[c, r] = weight == 0 ? float.NaN : (float)(sum / weight);
    }

    /// <summary>Normalised 2-D convolution (weights renormalised over valid cells; NaN cells ignored), e.g. a focal mean or Gaussian blur.</summary>
    public static RasterGrid Convolve(GpuContext gpu, RasterGrid raster, float[,] kernelWeights, int band = 0)
    {
        var acc = gpu.Accelerator;
        var kernel = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView2D<float, Stride2D.DenseX>, ArrayView2D<float, Stride2D.DenseX>, ArrayView2D<float, Stride2D.DenseX>>(ConvolveKernel);
        var data = raster.Bands[band].Select(v => raster.IsNoData(v) ? float.NaN : v).ToArray();
        using var input = acc.Allocate2DDenseX<float>(new LongIndex2D(raster.Width, raster.Height));
        input.View.BaseView.CopyFromCPU(data);
        int kh = kernelWeights.GetLength(0), kw = kernelWeights.GetLength(1);
        var kflat = new float[kw * kh];
        for (int y = 0; y < kh; y++) for (int x = 0; x < kw; x++) kflat[y * kw + x] = kernelWeights[y, x];
        using var kbuf = acc.Allocate2DDenseX<float>(new LongIndex2D(kw, kh));
        kbuf.View.BaseView.CopyFromCPU(kflat);
        using var output = acc.Allocate2DDenseX<float>(new LongIndex2D(raster.Width, raster.Height));
        kernel(new Index2D(raster.Width, raster.Height), input.View, kbuf.View, output.View);
        acc.Synchronize();
        return raster.WithBand(output.View.BaseView.GetAsArray(), float.NaN);
    }

    private static void IdwKernel(Index2D idx, ArrayView<double> px, ArrayView<double> py, ArrayView<double> pz, ArrayView2D<float, Stride2D.DenseX> output,
        double originX, double originY, double cellX, double cellY, double power)
    {
        double x = originX + (idx.X + 0.5) * cellX, y = originY - (idx.Y + 0.5) * cellY;
        double num = 0, den = 0;
        for (int i = 0; i < px.IntLength; i++)
        {
            double dx = px[i] - x, dy = py[i] - y, d2 = dx * dx + dy * dy;
            if (d2 < 1e-24) { output[idx.X, idx.Y] = (float)pz[i]; return; }
            double wgt = 1.0 / Math.Pow(d2, power / 2);
            num += wgt * pz[i]; den += wgt;
        }
        output[idx.X, idx.Y] = (float)(num / den);
    }

    /// <summary>Inverse-distance-weighted surface of point samples on a north-up grid (all samples per cell, brute force on the device).</summary>
    public static RasterGrid Idw(GpuContext gpu, double[] xs, double[] ys, double[] zs, Raster.GeoTransform transform, int width, int height,
        double power = 2, GeoPandas.Net.Geometry.Crs.CoordinateReferenceSystem? crs = null)
    {
        var acc = gpu.Accelerator;
        var kernel = acc.LoadAutoGroupedStreamKernel<Index2D, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView2D<float, Stride2D.DenseX>, double, double, double, double, double>(IdwKernel);
        using var bx = acc.Allocate1D(xs); using var by = acc.Allocate1D(ys); using var bz = acc.Allocate1D(zs);
        using var output = acc.Allocate2DDenseX<float>(new LongIndex2D(width, height));
        kernel(new Index2D(width, height), bx.View, by.View, bz.View, output.View, transform.OriginX, transform.OriginY, transform.PixelWidth, Math.Abs(transform.PixelHeight), power);
        acc.Synchronize();
        return new RasterGrid(width, height, [output.View.BaseView.GetAsArray()], transform, crs) { BandNames = ["idw"] };
    }
}
