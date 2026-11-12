using System.Net;
using System.Text;
using AquaCulture.ComputerVision;
using AquaCulture.ComputerVision.MediaPipe;
using AquaCulture.Core.Species;
using MediaPipeNet.Imaging;
using SixLabors.ImageSharp.PixelFormats;

namespace AquaCulture.Tests;

public class VisionV12Tests
{
    /// <summary>A fish-like silhouette: ellipse body plus a tail, rotated by <paramref name="angleDeg"/>, on a green background.</summary>
    private static Rgba32[] Fish(int w, int h, double cx, double cy, double halfLength, double halfDepth, double angleDeg)
    {
        var px = new Rgba32[w * h];
        double a = angleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx, dy = y - cy, u = dx * c + dy * s, v = -dx * s + dy * c;
                bool body = (u / halfLength) * (u / halfLength) + (v / halfDepth) * (v / halfDepth) <= 1;
                double tailU = u - halfLength * 0.9;
                bool tail = tailU > 0 && tailU < halfLength * 0.25 && Math.Abs(v) < tailU * 0.9;
                px[y * w + x] = body || tail ? new Rgba32(160, 165, 175) : new Rgba32(35, 85, 55);
            }
        return px;
    }

    [Fact]
    public void Principal_axis_measurement_handles_rotation_without_a_model()
    {
        int w = 400, h = 300;
        var mask = new float[w * h];
        double a = 35 * Math.PI / 180;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x - 200, dy = y - 150, u = dx * Math.Cos(a) + dy * Math.Sin(a), v = -dx * Math.Sin(a) + dy * Math.Cos(a);
                if (Math.Abs(u) <= 120 && Math.Abs(v) <= 20) mask[y * w + x] = 1;
            }
        var m = FishMeasurer.FromMask(mask, w, h, w * 2, h * 2, cmPerPixel: 0.05, species: SpeciesCatalog.Nila);   // mask at half resolution
        Assert.InRange(m.LengthPx, 480 * 0.98, 480 * 1.03);
        Assert.InRange(m.DepthPx, 80 * 0.9, 80 * 1.15);
        Assert.InRange(Math.Abs(m.AngleDeg), 33, 37);
        Assert.Equal(m.LengthPx * 0.05, m.LengthCm!.Value, 1);
        Assert.Equal(SpeciesCatalog.Nila.WeightFromLength(m.LengthCm.Value), m.WeightG!.Value, 1);
        Assert.True(m.Plausible);
    }

    [Fact]
    public void MagicTouch_measures_a_tilted_fish_and_follows_it_through_frames()
    {
        int w = 640, h = 480;
        using var measurer = new FishMeasurer(SpeciesCatalog.Nila, cmPerPixel: FishMeasurer.ScaleFromReference(600, 30));   // 30 cm board = 600 px
        using var img = MPImage.FromPixels(Fish(w, h, 300, 230, 180, 42, -25), w, h);
        var m = measurer.Measure(img, 300f / w, 230f / h);
        double trueLength = 2 * 180 * 1.0 + 180 * 0.25 - 180 * 0.1;           // body + tail beyond the body edge
        Assert.True(m.Plausible, $"{m}");
        Assert.InRange(m.LengthPx, trueLength * 0.93, trueLength * 1.05);
        Assert.InRange(m.AngleDeg, -30, -20);
        Assert.InRange(m.LengthCm!.Value, trueLength * 0.05 * 0.93, trueLength * 0.05 * 1.05);
        Assert.True(m.WeightG > 0);

        // the fish swims to the right over three frames; the tap starts at its first position and follows the centroid
        var frames = new[] { 260, 300, 340 }.Select(x => MPImage.FromPixels(Fish(w, h, x, 230, 180, 42, -25), w, h)).ToList();
        try
        {
            var (median, all) = measurer.MeasureVideo(frames, 260f / w, 230f / h);
            Assert.Equal(3, all.Count);
            Assert.All(all, f => Assert.True(f.Plausible));
            Assert.InRange(median!.LengthPx, trueLength * 0.93, trueLength * 1.05);
            Assert.True(all[2].CentroidX > all[0].CentroidX);
        }
        finally { foreach (var f in frames) f.Dispose(); }
    }

    private sealed class FakeHub : HttpMessageHandler
    {
        public int Downloads;
        public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            var path = r.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                string license = path.Contains("agpl", StringComparison.Ordinal) ? "agpl-3.0" : "apache-2.0";
                var json = $$"""{"id":"x","pipeline_tag":"object-detection","cardData":{"license":"{{license}}"},"gated":false,"lastModified":"2026-05-01T00:00:00.000Z","siblings":[{"rfilename":"README.md"},{"rfilename":"fish.onnx"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            if (r.Headers.IfNoneMatch.Any(e => e.Tag == "\"v1\"")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            Downloads++;
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("ONNX-BYTES")) };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task Model_hub_reads_licences_caches_downloads_and_refuses_copyleft_weights()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aqua-hub-{Guid.NewGuid():N}");
        var api = new FakeHub();
        var hub = new ModelHub(dir, token: "", endpoint: "https://hub.test", http: new HttpClient(api));
        try
        {
            var info = await hub.GetModelInfoAsync("farm/fish-detector");
            Assert.Equal("apache-2.0", info.License);
            Assert.True(info.IsPermissive);
            Assert.Equal(["fish.onnx"], info.OnnxFiles);
            Assert.False((await hub.GetModelInfoAsync("someone/agpl-yolo")).IsPermissive);

            var p1 = await hub.DownloadAsync("farm/fish-detector", "fish.onnx");
            var p2 = await hub.DownloadAsync("farm/fish-detector", "fish.onnx");           // 304 → cached
            Assert.Equal(p1, p2);
            Assert.Equal(1, api.Downloads);
            Assert.Equal("ONNX-BYTES", File.ReadAllText(p1));
            Assert.Contains("farm--fish-detector", p1);
            api.Offline = true;
            Assert.Equal(p1, await hub.DownloadAsync("farm/fish-detector", "fish.onnx"));   // offline → cached copy
            api.Offline = false;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hub.LoadDetectorAsync("someone/agpl-yolo", "fish.onnx", ["fish"]));
            Assert.Contains("agpl-3.0", ex.Message);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
