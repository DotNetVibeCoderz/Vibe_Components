using System.Globalization;

using System.Text.Json.Nodes;
using Agritech.Core.Data;
using Agritech.Core.Geo;
using Agritech.Core.Hosting;

namespace Agritech.RemoteSensing;

/// <summary>Satellite scene metadata returned from a catalog search.</summary>
public sealed record SceneInfo(string Id, SatelliteSensor Sensor, DateTimeOffset Acquired, double CloudCoverPct, GeoBounds Footprint,
    IReadOnlyDictionary<string, string> Assets);

public interface ISceneCatalog
{
    Task<IReadOnlyList<SceneInfo>> SearchAsync(GeoBounds area, DateOnly from, DateOnly to, SatelliteSensor sensor = SatelliteSensor.Sentinel2,
        double maxCloudPct = 30, int limit = 20, CancellationToken ct = default);
}

/// <summary>
/// STAC API client (default: Element 84 Earth Search, public, no key) for Sentinel-2 L2A and Landsat C2 L2.
/// Assets are Cloud-Optimized GeoTIFF URLs readable by GDAL or any COG reader.
/// </summary>
public sealed class StacSceneCatalog(HttpClient http, string endpoint = "https://earth-search.aws.element84.com/v1") : ISceneCatalog
{
    public StacSceneCatalog() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }) { }

    public async Task<IReadOnlyList<SceneInfo>> SearchAsync(GeoBounds area, DateOnly from, DateOnly to, SatelliteSensor sensor = SatelliteSensor.Sentinel2,
        double maxCloudPct = 30, int limit = 20, CancellationToken ct = default)
    {
        string collection = sensor switch
        {
            SatelliteSensor.Sentinel2 => "sentinel-2-l2a",
            SatelliteSensor.Landsat8 or SatelliteSensor.Landsat9 => "landsat-c2-l2",
            _ => throw new NotSupportedException($"{sensor} is not available from this STAC endpoint."),
        };
        var body = new JsonObject
        {
            ["collections"] = new JsonArray(collection),
            ["bbox"] = new JsonArray(area.MinLon, area.MinLat, area.MaxLon, area.MaxLat),
            ["datetime"] = string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd}T00:00:00Z/{to:yyyy-MM-dd}T23:59:59Z"),
            ["limit"] = limit,
            ["query"] = new JsonObject { ["eo:cloud_cover"] = new JsonObject { ["lt"] = maxCloudPct } },
        };
        using var resp = await http.PostAsync($"{endpoint}/search", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Parse(root!, sensor);
    }

    public static IReadOnlyList<SceneInfo> Parse(JsonNode root, SatelliteSensor sensor)
    {
        var list = new List<SceneInfo>();
        foreach (var f in root["features"]!.AsArray())
        {
            var p = f!["properties"]!;
            var bbox = f["bbox"]!.AsArray();
            var assets = new Dictionary<string, string>();
            if (f["assets"] is JsonObject a)
                foreach (var kv in a) if (kv.Value?["href"]?.GetValue<string>() is { } href) assets[kv.Key] = href;
            list.Add(new SceneInfo(
                f["id"]!.GetValue<string>(), sensor,
                DateTimeOffset.Parse(p["datetime"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                p["eo:cloud_cover"]?.GetValue<double>() ?? double.NaN,
                new GeoBounds(bbox[1]!.GetValue<double>(), bbox[0]!.GetValue<double>(), bbox[3]!.GetValue<double>(), bbox[2]!.GetValue<double>()),
                assets));
        }
        return list.OrderBy(s => s.CloudCoverPct).ToList();
    }
}

/// <summary>Generates realistic synthetic multispectral field imagery for demos, tests and benchmarking.</summary>
public static class SyntheticScene
{
    /// <summary>
    /// Creates an image over bounds where vigour varies smoothly with optional stress patches.
    /// </summary>
    public static MultispectralImage Create(GeoBounds bounds, double pixelSizeM = 10, int seed = 7, double meanVigour = 0.7,
        int stressPatches = 3, SatelliteSensor sensor = SatelliteSensor.Sentinel2, DateTimeOffset? acquired = null)
    {
        var rng = new Random(seed);
        var gt = GeoTransform.FromBounds(bounds, pixelSizeM, out int w, out int h);
        var patches = Enumerable.Range(0, stressPatches)
            .Select(_ => (X: rng.NextDouble() * w, Y: rng.NextDouble() * h, R: (0.08 + rng.NextDouble() * 0.15) * Math.Max(w, h), S: 0.3 + rng.NextDouble() * 0.5))
            .ToArray();
        float[] blue = new float[w * h], green = new float[w * h], red = new float[w * h], re = new float[w * h], nir = new float[w * h], swir = new float[w * h];
        double fx = rng.NextDouble() * 3 + 1, fy = rng.NextDouble() * 3 + 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = meanVigour + 0.12 * Math.Sin(fx * x / w * Math.PI) * Math.Cos(fy * y / h * Math.PI);
                foreach (var p in patches)
                {
                    double d = Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));
                    v -= p.S * Math.Exp(-d * d / (2 * p.R * p.R / 4));
                }
                v = Math.Clamp(v + rng.NextGaussian(0, 0.03), 0.02, 0.98);
                int i = y * w + x;
                // Linear mixing between bare soil and full canopy reflectance spectra.
                red[i] = (float)(0.20 * (1 - v) + 0.03 * v);
                nir[i] = (float)(0.28 * (1 - v) + 0.50 * v);
                green[i] = (float)(0.15 * (1 - v) + 0.08 * v);
                blue[i] = (float)(0.12 * (1 - v) + 0.03 * v);
                re[i] = (float)(0.24 * (1 - v) + 0.25 * v);
                swir[i] = (float)(0.32 * (1 - v) + 0.17 * v);
            }
        return new MultispectralImage(sensor, acquired ?? DateTimeOffset.UtcNow, $"SYN-{seed}")
            .With(SpectralBand.Blue, new RasterBand(w, h, gt, blue, "Blue"))
            .With(SpectralBand.Green, new RasterBand(w, h, gt, green, "Green"))
            .With(SpectralBand.Red, new RasterBand(w, h, gt, red, "Red"))
            .With(SpectralBand.RedEdge1, new RasterBand(w, h, gt, re, "RedEdge"))
            .With(SpectralBand.Nir, new RasterBand(w, h, gt, nir, "NIR"))
            .With(SpectralBand.Swir1, new RasterBand(w, h, gt, swir, "SWIR1"));
    }
}

public static class RemoteSensingBuilderExtensions
{
    public static AgritechBuilder AddRemoteSensing(this AgritechBuilder builder)
    {
        builder.TryAddModule("RemoteSensing");
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<ISceneCatalog>(builder.Services, _ => new StacSceneCatalog());
        return builder;
    }
}
