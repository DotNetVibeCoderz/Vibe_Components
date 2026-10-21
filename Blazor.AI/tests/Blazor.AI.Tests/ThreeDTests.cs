using System.Text.Json;
using Blazor.AI.ThreeD;

namespace Blazor.AI.Tests;

public class ThreeDTests
{
    [Fact]
    public void Force_layout_is_deterministic_and_bounded()
    {
        string[] nodes = ["a", "b", "c", "d", "e"];
        (string, string, double)[] edges = [("a", "b", 1), ("b", "c", 1), ("c", "d", 1), ("d", "e", 1)];
        var first = Layout3D.Force(nodes, edges, radius: 4);
        var second = Layout3D.Force(nodes, edges, radius: 4);

        Assert.All(nodes, n => Assert.Equal(first[n], second[n]));
        Assert.All(first.Values, p => Assert.True(Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]) <= 4 * 1.15 + 1e-6));
        // Linked nodes end up closer than the chain's ends.
        Assert.True(Layout3D.Distance(first["a"], first["b"]) < Layout3D.Distance(first["a"], first["e"]));
    }

    [Fact]
    public void Tree_layout_puts_children_below_parents()
    {
        var children = new Dictionary<string, string[]> { ["root"] = ["left", "right"], ["left"] = [], ["right"] = [] };
        var pos = Layout3D.Tree("root", n => n, n => children[n]);

        Assert.True(pos["left"][1] < pos["root"][1]);
        Assert.True(pos["left"][0] < pos["right"][0]);
        Assert.Equal(0, pos["root"][0], 6);
    }

    [Fact]
    public void Pca_projects_to_three_dimensions_and_keeps_order_of_variance()
    {
        var rng = new Random(1);
        var rows = Enumerable.Range(0, 200).Select(_ => new[] { rng.NextDouble() * 10, rng.NextDouble() * 2, rng.NextDouble() * 0.1, rng.NextDouble() * 0.01 }).ToList();
        var p = Layout3D.Pca3(rows);

        Assert.Equal(200, p.Length);
        Assert.All(p, x => Assert.Equal(3, x.Length));
        double Var(int c) => p.Select(x => x[c]).Average(v => v * v);
        Assert.True(Var(0) > Var(1) && Var(1) > Var(2));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.37)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void Palette_ramps_return_hex_colours(double t)
    {
        Assert.Matches("^#[0-9a-f]{6}$", ThreeDPalette.Ramp(t));
        Assert.Matches("^#[0-9a-f]{6}$", ThreeDPalette.Heat(t));
        Assert.Equal(ThreeDPalette.Category(0), ThreeDPalette.Category(ThreeDPalette.Categorical.Count));
    }

    [Theory]
    [InlineData("Hello Kawi!", "Wave")]
    [InlineData("Halo, apa kabar?", "Wave")]
    [InlineData("Where is the museum exit?", "Point")]
    [InlineData("Tell me a story", null)]
    public void Avatar_gestures_follow_what_the_user_said(string text, string? gesture) =>
        Assert.Equal(gesture, AvatarBrain.GestureFor(text));

    [Fact]
    public void Avatar_emotions_map_to_clips_and_speech_is_cleaned()
    {
        Assert.Equal(AvatarEmotion.Happy, AvatarBrain.Parse("joy"));
        Assert.Equal("Happy", AvatarBrain.ClipFor(AvatarEmotion.Happy));
        Assert.Equal(AvatarEmotion.Sad, AvatarBrain.Guess("Maaf, saya tidak tahu."));
        Assert.Equal("Visit the temple at dawn.", AvatarBrain.ForSpeech("**Visit** the [temple](https://x.y) at dawn."));
    }

    [Fact]
    public void Companion_memory_recalls_related_facts_and_tracks_mood()
    {
        var store = new CompanionMemoryStore();
        store.Remember("User likes durian ice cream");
        store.Remember("User lives in Bandung");
        store.Remember("user likes durian ice cream"); // duplicate ignored
        Assert.Equal(2, store.Memories.Count);
        Assert.Single(store.Recall("any durian dessert ideas?"));

        for (var i = 0; i < 5; i++)
        {
            store.Feel(0.9, 0.4);
        }

        Assert.Equal(AvatarEmotion.Happy, store.Mood);
        Assert.True(store.Rapport > 0.3);
    }

    [Fact]
    public void Scene_objects_serialize_without_nulls_in_camel_case()
    {
        Object3D[] objects = [Object3D.Box("b1", [0, 0.5, 0], [1, 1, 1], "#ff0000") with { Animate = Animate3D.Spin() }];
        var json = JsonSerializer.Serialize(objects, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        Assert.Contains("\"kind\":\"box\"", json);
        Assert.Contains("\"animate\":{\"type\":\"spin\"", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Live_metrics_flag_anomalies_after_a_shock()
    {
        var m = new LiveMetric("Latency", "ms", 100, 2, 120, higherIsBetter: false);
        var rng = new Random(2);
        for (var i = 0; i < 30; i++)
        {
            m.Step(rng);
        }

        Assert.False(m.Anomalous);
        m.Baseline = 160;
        m.Step(rng);
        Assert.True(m.Anomalous);
        Assert.True(m.Attainment < 1);
    }
}
