using System.Text.Json;
using Blazor.AI.MediaPipe;
using MediaPipeNet;
using MediaPipeNet.Tasks.Vision;

namespace Blazor.AI.Tests;

public class VisionTests
{
    private static NormalizedLandmark P(float x, float y, float z = 0) => new() { X = x, Y = y, Z = z };

    [Fact]
    public void Angle_at_a_right_angle_is_90_degrees()
    {
        Assert.Equal(90, VisionMath.Angle(P(0, 0), P(0, 1), P(1, 1)), 3);
        Assert.Equal(180, VisionMath.Angle(P(0, 0), P(0.5f, 0), P(1, 0)), 3);
        Assert.Equal(90, VisionMath.Angle3(new Landmark { X = 1 }, new Landmark(), new Landmark { Y = 1 }), 3);
    }

    [Fact]
    public void Aspect_ratio_changes_image_plane_angles()
    {
        // A 45° diagonal in normalized space becomes shallower on a wide frame.
        var wide = VisionMath.Angle(P(1, 0), P(0, 0), P(1, 1), aspect: 16.0 / 9);
        Assert.True(wide < 45);
    }

    [Fact]
    public void Open_hand_counts_five_fingers_and_fist_counts_none()
    {
        Assert.Equal(5, VisionMath.CountFingers(Hand(open: true)));
        Assert.Equal(0, VisionMath.CountFingers(Hand(open: false)));
    }

    // A synthetic right hand, wrist at the bottom; open = fingers stretched up, closed = tips curled to the palm.
    private static HandLandmarks Hand(bool open)
    {
        var l = new NormalizedLandmark[21];
        l[0] = P(0.5f, 0.9f);
        float[] baseX = [0.40f, 0.45f, 0.50f, 0.55f, 0.60f];
        for (var f = 0; f < 5; f++)
        {
            var mcp = f == 0 ? 1 : 1 + f * 4;
            for (var j = 0; j < 4; j++)
            {
                var idx = mcp + j;
                if (idx > 20)
                {
                    continue;
                }

                var x = f == 0 ? 0.42f - (open ? j * 0.06f : j * 0.005f) : baseX[f];
                var y = open ? 0.75f - j * 0.08f : (j < 2 ? 0.75f - j * 0.05f : 0.72f + (j - 2) * 0.03f);
                if (f == 0 && !open)
                {
                    (x, y) = (0.45f + j * 0.02f, 0.78f);
                }

                l[idx] = P(x, y);
            }
        }

        return new HandLandmarks(new Category(0, 1, "Right"), l, [.. Enumerable.Range(0, 21).Select(_ => new Landmark())], 1, default);
    }

    [Fact]
    public void Masks_are_downsampled_and_encoded_as_bytes()
    {
        var data = new float[400 * 300];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = i % 400 < 200 ? 1 : 0; // left half is subject
        }

        var m = Overlay.Mask(new SegmentationMask(400, 300, data), "cutout", maxWidth: 100);
        var bytes = Convert.FromBase64String(m.Data);
        Assert.Equal(100, m.Width);
        Assert.Equal(75, m.Height);
        Assert.Equal(m.Width * m.Height, bytes.Length);
        Assert.Equal(255, bytes[10]);
        Assert.Equal(0, bytes[90]);
    }

    [Fact]
    public void Face_gallery_matches_the_closest_person()
    {
        var gallery = new FaceGallery();
        gallery.Enroll("Sari", [1, 0, 0]);
        gallery.Enroll("Budi", [0, 1, 0]);
        var (person, similarity) = gallery.Match([0.9f, 0.1f, 0]);
        Assert.Equal("Sari", person?.Name);
        Assert.True(similarity > 0.9);
        gallery.Remove("sari");
        Assert.Single(gallery.People);
    }

    [Fact]
    public void Overlay_serializes_camel_case_without_nulls()
    {
        var overlay = new VisionOverlay { Status = "1 face" };
        overlay.Boxes.Add(new OverlayBox { X = 0.1f, Y = 0.2f, W = 0.3f, H = 0.4f, Label = "face" });
        var json = JsonSerializer.Serialize(overlay, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.Contains("\"status\":\"1 face\"", json);
        Assert.Contains("\"boxes\":[{", json);
        Assert.DoesNotContain("\"mask\"", json);
    }

    [Fact]
    public void Landmark_bounds_are_clamped_to_the_frame()
    {
        var (x, y, w, h) = Overlay.Bounds([P(-0.1f, 0.2f), P(0.5f, 1.2f)], pad: 0);
        Assert.Equal(0, x);
        Assert.Equal(0.2f, y, 3);
        Assert.Equal(0.5f, w, 3);
        Assert.Equal(0.8f, h, 3);
    }
}
