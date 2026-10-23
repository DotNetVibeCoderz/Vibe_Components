using System.Collections.Concurrent;
using System.Text.Json;
using CesiumNet.Interop;
using CesiumNet.Realtime;

namespace CesiumNet.Tests;

/// <summary>Records bridge calls and returns canned results.</summary>
internal sealed class FakeTransport : ICesiumTransport
{
    public ConcurrentQueue<(string Method, string Args)> Calls { get; } = new();
    public Func<string, string, string?> Responder { get; set; } = (_, _) => null;

    public ValueTask<string?> InvokeAsync(string method, string argsJson, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((method, argsJson));
        return ValueTask.FromResult(Responder(method, argsJson));
    }
}

public class InteropTests
{
    private static (CesiumController Controller, FakeTransport Transport) CreateReady()
    {
        var t = new FakeTransport();
        var c = new CesiumController(t);
        c.HandleHostEvent("ready", null);
        return (c, t);
    }

    [Fact]
    public void GeoPoint_SerializesAsCompactArray()
    {
        var json = JsonSerializer.Serialize(new GeoPoint(106.8, -6.2, 10), CesiumJsonContext.Default.GeoPoint);
        Assert.Equal("[106.8,-6.2,10]", json);
        Assert.Equal(new GeoPoint(106.8, -6.2, 10), JsonSerializer.Deserialize("[106.8,-6.2,10]", CesiumJsonContext.Default.GeoPoint));
        Assert.Equal(new GeoPoint(1, 2, 0), JsonSerializer.Deserialize("[1,2]", CesiumJsonContext.Default.GeoPoint));
        Assert.Equal(new GeoPoint(1, 2, 0), JsonSerializer.Deserialize("[1,2,null]", CesiumJsonContext.Default.GeoPoint));
    }

    [Fact]
    public void Color_HexRoundTrip()
    {
        var c = CesiumColor.FromHex("#1F4FE0");
        Assert.Equal("#1F4FE0FF", c.ToCss());
        Assert.Equal("#1F4FE080", c.WithAlpha(0.5f).ToCss());
        Assert.Equal("#FFFFFFFF", CesiumColor.FromHex("#fff").ToCss());
        Assert.Equal("\"#00000000\"", JsonSerializer.Serialize(CesiumColor.Transparent, CesiumJsonContext.Default.CesiumColor));
        Assert.Throws<FormatException>(() => CesiumColor.FromHex("#12345"));
    }

    [Fact]
    public void EntityOptions_OmitNullsUseCamelCaseAndStringEnums()
    {
        var e = new EntityOptions
        {
            Name = "Monas",
            Position = new GeoPoint(106.8, -6.2),
            Point = new PointGraphics { PixelSize = 10, HeightReference = HeightReference.ClampToGround },
            Polyline = new PolylineGraphics { Positions = [new(0, 0), new(1, 1)], Material = CesiumMaterial.Glow(CesiumColor.Cyan) },
        };
        var json = JsonSerializer.Serialize(e, CesiumJsonContext.Default.EntityOptions);
        Assert.Contains("\"name\":\"Monas\"", json);
        Assert.Contains("\"position\":[106.8,-6.2,0]", json);
        Assert.Contains("\"heightReference\":\"ClampToGround\"", json);
        Assert.Contains("\"kind\":\"PolylineGlow\"", json);
        Assert.DoesNotContain("billboard", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void ImplicitConversions()
    {
        CesiumColor fromString = "#FF0000";
        Assert.Equal(1f, fromString.R);
        CesiumMaterial m = CesiumColor.Red;
        Assert.Equal(MaterialKind.Color, m.Kind);
    }

    [Fact]
    public void JsLiteral_EscapesSafely()
    {
        var lit = CesiumScripts.ToJsLiteral("a\"b</script>\n");
        Assert.StartsWith("\"", lit);
        Assert.DoesNotContain("</script>", lit);
        Assert.DoesNotContain("\n", lit);
    }

    [Fact]
    public void BridgeScript_IsEmbedded()
    {
        Assert.Contains("global.CesiumNet", CesiumScripts.BridgeScript);
        Assert.Contains("'entities.add'", CesiumScripts.BridgeScript);
    }

    [Fact]
    public async Task Calls_AreQueuedUntilReady()
    {
        var t = new FakeTransport();
        var c = new CesiumController(t);
        var call = c.Camera.FlyHomeAsync().AsTask();
        await Task.Delay(50);
        Assert.Empty(t.Calls);
        Assert.False(call.IsCompleted);

        c.HandleHostEvent("ready", null);
        await call;
        Assert.Single(t.Calls);
        Assert.True(c.IsReady);
    }

    [Fact]
    public async Task Methods_SendExpectedProtocol()
    {
        var (c, t) = CreateReady();
        t.Responder = (m, _) => m == "entities.add" ? "\"e-1\"" : null;

        var id = await c.Entities.AddAsync(new EntityOptions { Position = new GeoPoint(1, 2) });
        await c.Camera.FlyToAsync(106.8, -6.2, 1000, heading: 90, pitch: -30, duration: 2);
        await c.Camera.MoveAsync(CameraDirection.Left, 50);

        Assert.Equal("e-1", id);
        var calls = t.Calls.ToArray();
        Assert.Equal(("entities.add", "[{\"position\":[1,2,0]}]"), calls[0]);
        Assert.Equal("camera.flyTo", calls[1].Method);
        Assert.Equal("[{\"destination\":[106.8,-6.2,1000],\"heading\":90,\"pitch\":-30,\"duration\":2}]", calls[1].Args);
        Assert.Equal(("camera.move", "[\"Left\",50]"), calls[2]);
    }

    [Fact]
    public async Task Results_AreDeserialized()
    {
        var (c, t) = CreateReady();
        t.Responder = (m, _) => m switch
        {
            "camera.getState" => "{\"position\":[106.8,-6.2,2500],\"heading\":10,\"pitch\":-45,\"roll\":0,\"viewRectangle\":{\"west\":106,\"south\":-7,\"east\":107,\"north\":-6}}",
            "terrain.sample" => "[[106.8,-6.2,12.5]]",
            "scene.screenshot" => "\"data:image/png;base64,iVBORw0KGgo=\"",
            _ => null,
        };

        var state = await c.Camera.GetStateAsync();
        Assert.Equal(2500, state.Position.Height);
        Assert.Equal(-45, state.Pitch);
        Assert.Equal(new GeoRectangle(106, -7, 107, -6), state.ViewRectangle);

        var h = await c.Terrain.GetHeightAsync(new GeoPoint(106.8, -6.2));
        Assert.Equal(12.5, h);

        var png = await c.Scene.CaptureScreenshotPngAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png);
    }

    [Fact]
    public void Events_AreRaisedWithTypedPayloads()
    {
        var (c, _) = CreateReady();
        PickResult? pick = null;
        CameraState? cam = null;
        string? selected = "unset";
        DrawResult? draw = null;
        CesiumError? error = null;
        c.LeftClick += (_, p) => pick = p;
        c.CameraChanged += (_, s) => cam = s;
        c.SelectedEntityChanged += (_, id) => selected = id;
        c.DrawCompleted += (_, d) => draw = d;
        c.Error += (_, e) => error = e;

        c.HandleHostEvent("leftClick", "{\"kind\":\"Entity\",\"screen\":{\"x\":10,\"y\":20},\"position\":[1,2,3],\"entityId\":\"monas\",\"properties\":{\"height\":\"132\"}}");
        c.HandleHostEvent("cameraChanged", "{\"position\":[1,2,3],\"heading\":0,\"pitch\":-90,\"roll\":0}");
        c.HandleHostEvent("selectedEntityChanged", "null");
        c.HandleHostEvent("drawCompleted", "{\"mode\":\"MeasureArea\",\"positions\":[[0,0],[1,0],[1,1],[0,1]]}");
        c.HandleHostEvent("error", "{\"message\":\"boom\",\"source\":\"render\"}");

        Assert.Equal(PickKind.Entity, pick!.Kind);
        Assert.Equal("monas", pick.EntityId);
        Assert.Equal("132", pick.Properties!["height"]);
        Assert.Equal(new Cartesian2(10, 20), pick.Screen);
        Assert.Equal(-90, cam!.Pitch);
        Assert.Null(selected);
        Assert.Equal(DrawMode.MeasureArea, draw!.Mode);
        Assert.InRange(draw.Area, 12_000e6, 12_700e6);
        Assert.Equal("render: boom", error!.ToString());
    }

    [Fact]
    public void DrawResult_RectangleAreaFromTwoCorners()
    {
        var r = new DrawResult { Mode = DrawMode.Rectangle, Positions = [new(0, 0), new(1, 1)] };
        Assert.Equal(12_364e6, r.Area, 0.005 * 12_364e6);
    }

    [Fact]
    public async Task DrawAsync_CompletesFromEvent()
    {
        var (c, t) = CreateReady();
        var task = c.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.Polyline }).AsTask();
        await Task.Delay(20);
        Assert.Contains(t.Calls, x => x.Method == "draw.start");
        c.HandleHostEvent("drawCompleted", "{\"mode\":\"Polyline\",\"entityId\":\"draw-1\",\"positions\":[[0,0],[0,1]]}");
        var result = await task;
        Assert.Equal("draw-1", result!.EntityId);
        Assert.Equal(110_574, result.Length, 1d);
    }

    [Fact]
    public async Task DrawAsync_NewDrawingCompletesPreviousWithNull()
    {
        var (c, _) = CreateReady();
        var first = c.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.Polygon }).AsTask();
        var second = c.Drawing.DrawAsync(new DrawOptions { Mode = DrawMode.Point }).AsTask();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        c.HandleHostEvent("drawCompleted", "{\"mode\":\"Point\",\"positions\":[[1,2]]}");
        Assert.Equal(DrawMode.Point, (await second.WaitAsync(TimeSpan.FromSeconds(5)))!.Mode);
    }

    [Fact]
    public async Task Failure_BeforeReady_FaultsQueuedCalls()
    {
        var c = new CesiumController(new FakeTransport());
        var call = c.Camera.FlyHomeAsync().AsTask();
        c.HandleHostFailure(new InvalidOperationException("CDN blocked"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        Assert.Equal("CDN blocked", ex.Message);
    }

    [Fact]
    public async Task RealtimeFeed_CoalescesLatestPerEntity()
    {
        var (c, t) = CreateReady();
        await using var feed = new RealtimeEntityFeed(c, TimeSpan.FromHours(1)); // manual flush only
        for (var i = 0; i < 100; i++) feed.Push("ship-1", new GeoPoint(i, 0));
        feed.Push("ship-2", new GeoPoint(5, 5), heading: 45);
        Assert.Equal(2, feed.PendingCount);

        await feed.FlushAsync();

        var call = Assert.Single(t.Calls, x => x.Method == "entities.setPositions");
        Assert.Contains("\"id\":\"ship-1\",\"position\":[99,0,0]", call.Args);
        Assert.Contains("\"heading\":45", call.Args);
        Assert.Equal(2, feed.PublishedCount);
        Assert.Equal(0, feed.PendingCount);
    }

    [Fact]
    public async Task RealtimeFeed_FlushesOnTimer()
    {
        var (c, t) = CreateReady();
        await using var feed = new RealtimeEntityFeed(c, TimeSpan.FromMilliseconds(20));
        feed.Push("uav", new GeoPoint(1, 1, 100));
        for (var i = 0; i < 50 && feed.PublishedCount == 0; i++) await Task.Delay(20);
        Assert.Equal(1, feed.PublishedCount);
        Assert.Contains(t.Calls, x => x.Method == "entities.setPositions");
    }
}
