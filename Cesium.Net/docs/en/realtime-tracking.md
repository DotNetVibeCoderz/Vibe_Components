[Bahasa Indonesia](../id/realtime-tracking.md)

# Realtime tracking

![Fleet tracking sample](../images/gallery-fleet.png)

Live feeds (AIS ships, ADS-B aircraft, UAV telemetry, buses, IoT sensors) often deliver far more messages than a viewer needs to render. `RealtimeEntityFeed` (namespace `CesiumNet.Realtime`) sits between any message source and the viewer.

```
SignalR / WebSocket / MQTT / REST ──Push()──► RealtimeEntityFeed ──every 100 ms──► Entities.UpdatePositionsAsync(batch)
          (any thread, never blocks)            latest-wins per id                    one interop call
```

- `Push` is thread-safe and lock-free. It keeps **only the newest update per entity id**, so 1,000 messages for one aircraft between flushes cost one update.
- Every `interval` (default 100 ms, i.e. 10 Hz), pending updates are sent in a **single** `UpdatePositionsAsync` call.
- `PublishedCount` and `PendingCount` help with monitoring. `FlushFailed` reports exceptions, for example when the viewer is gone. It is raised on a thread pool thread.
- `DisposeAsync` stops the loop. Updates still pending are dropped; call `FlushAsync()` first if you need them.

## The pattern

`UpdatePositionsAsync` silently skips ids that don't exist yet. Create an entity the first time an id is seen, then stream positions:

```csharp
using System.Collections.Concurrent;
using CesiumNet;
using CesiumNet.Realtime;

public sealed class TrackLayer(CesiumController cesium) : IAsyncDisposable
{
    private readonly RealtimeEntityFeed _feed = new(cesium, TimeSpan.FromMilliseconds(100));
    private readonly ConcurrentDictionary<string, byte> _known = new();

    public async ValueTask OnPositionAsync(string id, double lon, double lat, double alt, double? heading)
    {
        if (_known.TryAdd(id, 0))
        {
            await cesium.Entities.AddAsync(new EntityOptions
            {
                Id = id, Name = id,
                Position = new GeoPoint(lon, lat, alt),
                Billboard = new BillboardGraphics { Image = "/icons/arrow.png", Scale = 0.6, Rotation = heading ?? 0 },
                Label = new LabelGraphics { Text = id, PixelOffset = new Cartesian2(0, -22), Scale = 0.8,
                                            DistanceDisplayCondition = new DistanceDisplayCondition(0, 150_000) },
            });
            return;
        }
        _feed.Push(id, new GeoPoint(lon, lat, alt), heading);   // cheap; coalesced
    }

    public ValueTask DisposeAsync() => _feed.DisposeAsync();
}
```

`Heading` rotates models, boxes and cylinders (orientation) and billboards (clockwise rotation). Labels are not rotated.

## SignalR

Package `Microsoft.AspNetCore.SignalR.Client`. This works in Blazor Server, Blazor WebAssembly and Avalonia.

```csharp
using Microsoft.AspNetCore.SignalR.Client;

var tracks = new TrackLayer(cesium);
await using var hub = new HubConnectionBuilder()
    .WithUrl("https://example.com/hubs/fleet")
    .WithAutomaticReconnect()
    .Build();

hub.On<string, double, double, double, double?>("position",
    (id, lon, lat, alt, heading) => tracks.OnPositionAsync(id, lon, lat, alt, heading).AsTask());

await hub.StartAsync();
```

Server side, for example in an ASP.NET Core hub or a background service:

```csharp
await hubContext.Clients.All.SendAsync("position", "PK-ABC", 106.65, -6.12, 3_200.0, 87.0);
```

## WebSocket (raw JSON)

`System.Net.WebSockets.ClientWebSocket` works on desktop and in the browser (Blazor WebAssembly).

```csharp
using System.Net.WebSockets;
using System.Text.Json;

public sealed record PositionMessage(string Id, double Lon, double Lat, double Alt, double? Heading);

async Task RunWebSocketAsync(TrackLayer tracks, CancellationToken ct)
{
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri("wss://example.com/live"), ct);
    var buffer = new byte[16 * 1024];
    using var message = new MemoryStream();
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
    {
        var result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close) break;
        message.Write(buffer, 0, result.Count);
        if (!result.EndOfMessage) continue;

        // One message may carry an array of positions.
        var batch = JsonSerializer.Deserialize<PositionMessage[]>(message.GetBuffer().AsSpan(0, (int)message.Length), json) ?? [];
        message.SetLength(0);
        foreach (var p in batch) await tracks.OnPositionAsync(p.Id, p.Lon, p.Lat, p.Alt, p.Heading);
    }
}
```

If you publish with trimming or Native AOT, replace the reflection-based `JsonSerializer` call with a source-generated `JsonSerializerContext`.

## MQTT

Package `MQTTnet`. The snippet uses the 5.x API; 4.x uses `MqttFactory` and similar members. In Blazor WebAssembly, connect over MQTT-over-WebSockets (`.WithWebSocketServer(...)`), because raw TCP is not available in the browser.

```csharp
using MQTTnet;
using System.Text.Json;

var factory = new MqttClientFactory();
using var mqtt = factory.CreateMqttClient();
var tracks = new TrackLayer(cesium);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

mqtt.ApplicationMessageReceivedAsync += async e =>
{
    // Topic: fleet/{id}/position   Payload: {"lon":..,"lat":..,"alt":..,"heading":..}
    var id = e.ApplicationMessage.Topic.Split('/')[1];
    var p = JsonSerializer.Deserialize<PositionMessage>(e.ApplicationMessage.ConvertPayloadToString(), json);
    if (p is not null) await tracks.OnPositionAsync(id, p.Lon, p.Lat, p.Alt, p.Heading);
};

await mqtt.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("broker.example.com", 1883).Build());
await mqtt.SubscribeAsync(factory.CreateSubscribeOptionsBuilder().WithTopicFilter("fleet/+/position").Build());
```

## REST polling

```csharp
using System.Net.Http.Json;

async Task PollAsync(HttpClient http, TrackLayer tracks, CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
    do
    {
        var snapshot = await http.GetFromJsonAsync<PositionMessage[]>("https://example.com/api/vehicles", ct) ?? [];
        foreach (var v in snapshot) await tracks.OnPositionAsync(v.Id, v.Lon, v.Lat, v.Alt, v.Heading);
    }
    while (await timer.WaitForNextTickAsync(ct));
}
```

With slow polling, position jumps are visible. Two options smooth them out:

- Use `SampledPosition` with `Extrapolate = true` and append each fix with `AddSamplesAsync`. CesiumJS then interpolates between fixes, provided the clock runs in real time (`Multiplier = 1`, `ShouldAnimate = true`, `CurrentTime` ≈ now, slightly delayed).
- Use a short `Path` (`TrailTime`) to show recent history.

## Simulators and tests

`RealtimeEntityFeed` doesn't care where updates come from, so a timer that moves N vehicles is enough to load-test a scene:

```csharp
await using var feed = new RealtimeEntityFeed(cesium);
var rnd = new Random(1);
var ids = Enumerable.Range(0, 2_000).Select(i => $"veh-{i}").ToArray();
await cesium.Entities.AddRangeAsync(ids.Select(id => new EntityOptions
{
    Id = id, Position = new GeoPoint(106.8 + rnd.NextDouble() * 0.2, -6.3 + rnd.NextDouble() * 0.2),
    Point = new PointGraphics { PixelSize = 5, Color = CesiumColor.Lime },
}).ToList());

using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(20));        // 50 Hz producer
while (await tick.WaitForNextTickAsync(ct))
    foreach (var id in ids)
        feed.Push(id, new GeoPoint(106.8 + rnd.NextDouble() * 0.2, -6.3 + rnd.NextDouble() * 0.2));
// The viewer still receives only 10 batches per second.
```

## Host notes

| Host | Notes |
|---|---|
| Blazor Server | The feed runs on the server; each flush is one SignalR message to the browser. Keep the interval at 100 ms or more for many clients. Dispose the feed in the component's `DisposeAsync`. |
| Blazor WebAssembly | Everything runs in the browser. Use SignalR, WebSocket or MQTT-over-WebSocket clients. |
| Avalonia | Flushes are marshalled to the UI thread by the transport. `FlushFailed` arrives on the thread pool, so use `Dispatcher.UIThread.Post` for UI updates. |

Use `RequestRenderMode = true` for mostly static scenes. Each flush triggers a render automatically.

---
Made by Gravicode Studios, led by Kang Fadhil.
