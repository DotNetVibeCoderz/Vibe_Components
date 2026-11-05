using Agritech.Core.Geo;
using Agritech.Core.Hosting;

namespace Agritech.Drones;

/// <summary>A nadir drone photo with its GNSS position, yaw and ground sampling distance.</summary>
public sealed record GeotaggedImage(RgbImage Image, GeoCoordinate Center, double HeadingDeg, double GsdM);

public sealed record OrthomosaicResult(RgbImage Mosaic, GeoCoordinate TopLeft, double GsdM, double CoverageAreaHa, int ImagesUsed);

/// <summary>
/// Direct-georeferencing orthomosaic builder. Each output pixel is sampled from the photo whose centre is nearest
/// (minimises lens distortion and relief displacement — the "nearest-nadir" seamline rule). This assumes flat terrain
/// and accurate GNSS/IMU (e.g. RTK drones); for survey-grade output use full SfM (OpenDroneMap) and import the result.
/// </summary>
public static class OrthomosaicBuilder
{
    public static OrthomosaicResult Build(IReadOnlyList<GeotaggedImage> images, double? outputGsdM = null)
    {
        if (images.Count == 0) throw new ArgumentException("At least one image is required.");
        double gsd = outputGsdM ?? images.Max(i => i.GsdM);
        var origin = images[0].Center;
        // Compute footprint extents in the local plane.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var centers = new Point2[images.Count];
        for (int k = 0; k < images.Count; k++)
        {
            var im = images[k];
            centers[k] = GeoMath.ToLocal(origin, im.Center);
            double rx = im.Image.Width * im.GsdM / 2, ry = im.Image.Height * im.GsdM / 2, r = Math.Sqrt(rx * rx + ry * ry);
            minX = Math.Min(minX, centers[k].X - r); maxX = Math.Max(maxX, centers[k].X + r);
            minY = Math.Min(minY, centers[k].Y - r); maxY = Math.Max(maxY, centers[k].Y + r);
        }
        int w = (int)Math.Ceiling((maxX - minX) / gsd), h = (int)Math.Ceiling((maxY - minY) / gsd);
        if ((long)w * h > 400_000_000) throw new InvalidOperationException("Mosaic too large; increase output GSD.");
        var mosaic = new RgbImage(w, h);
        var covered = 0L;
        var used = new HashSet<int>();

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                var p = new Point2(minX + (x + 0.5) * gsd, maxY - (y + 0.5) * gsd);
                int best = -1; double bestD = double.MaxValue; int bx = 0, by = 0;
                for (int k = 0; k < images.Count; k++)
                {
                    var im = images[k];
                    var d = p - centers[k];
                    // rotate into image frame (heading clockwise from north; image up = heading)
                    double th = im.HeadingDeg * Math.PI / 180;
                    double u = d.X * Math.Cos(th) - d.Y * Math.Sin(th);
                    double v = d.X * Math.Sin(th) + d.Y * Math.Cos(th);
                    int ix = (int)(im.Image.Width / 2.0 + u / im.GsdM);
                    int iy = (int)(im.Image.Height / 2.0 - v / im.GsdM);
                    if (ix < 0 || iy < 0 || ix >= im.Image.Width || iy >= im.Image.Height) continue;
                    double dist = d.Length;
                    if (dist < bestD) { bestD = dist; best = k; bx = ix; by = iy; }
                }
                if (best < 0) continue;
                mosaic[x, y] = images[best].Image[bx, by];
                Interlocked.Increment(ref covered);
                lock (used) used.Add(best);
            }
        });
        var topLeft = GeoMath.FromLocal(origin, new Point2(minX, maxY));
        return new OrthomosaicResult(mosaic, topLeft, gsd, Math.Round(covered * gsd * gsd / 10_000, 4), used.Count);
    }
}

public static class DronesBuilderExtensions
{
    public static AgritechBuilder AddDrones(this AgritechBuilder builder)
    {
        builder.TryAddModule("Drones");
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IPlantDiseaseClassifier, LesionSeverityAnalyzer>(builder.Services);
        return builder;
    }
}
