using System.Globalization;
using System.Text;
using System.Text.Json;
using MQTTnet;

namespace Agritech.IoT;

/// <summary>Options for <see cref="MqttSensorTransport"/>.</summary>
public sealed record MqttOptions(string Host = "localhost", int Port = 1883, string TopicRoot = "agritech", string? FarmId = "farm1",
    string? Username = null, string? Password = null, bool UseTls = false, string? ClientId = null);

/// <summary>
/// MQTT transport (MQTTnet). Topic schema: <c>{root}/{farm}/{device}/{kind}[/{channel}]</c> with JSON payload
/// <c>{"v":12.3,"u":"%VWC","ts":"2026-01-01T00:00:00Z"}</c>. Plain numeric payloads are also accepted.
/// </summary>
public sealed class MqttSensorTransport(MqttOptions options) : ISensorTransport
{
    private IMqttClient? _client;

    public event Action<SensorReading>? ReadingReceived;

    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client = new MqttClientFactory().CreateMqttClient();
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
            .WithClientId(options.ClientId ?? $"agritech-{Guid.NewGuid():N}"[..20])
            .WithCleanSession();
        if (options.Username is not null) b = b.WithCredentials(options.Username, options.Password);
        if (options.UseTls) b = b.WithTlsOptions(o => o.UseTls());
        _client.ApplicationMessageReceivedAsync += e =>
        {
            if (TryParse(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString(), out var r)) ReadingReceived?.Invoke(r!);
            return Task.CompletedTask;
        };
        await _client.ConnectAsync(b.Build(), ct).ConfigureAwait(false);
        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter($"{options.TopicRoot}/{options.FarmId ?? "+"}/#").Build(), ct).ConfigureAwait(false);
    }

    public async Task PublishAsync(SensorReading reading, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Call ConnectAsync first.");
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(Topic(options, reading))
            .WithPayload(reading.ToJson())
            .Build();
        await _client.PublishAsync(msg, ct).ConfigureAwait(false);
    }

    public static string Topic(MqttOptions o, SensorReading r) =>
        $"{o.TopicRoot}/{o.FarmId ?? "default"}/{r.DeviceId}/{r.Kind.ToString().ToLowerInvariant()}" + (r.Channel != 0 ? $"/{r.Channel}" : "");

    /// <summary>Parses a topic + payload into a reading (used by the transport and by bridges).</summary>
    public static bool TryParse(string topic, string payload, out SensorReading? reading)
    {
        reading = null;
        var parts = topic.Split('/');
        if (parts.Length < 4 || !Enum.TryParse<SensorKind>(parts[3], true, out var kind)) return false;
        int ch = parts.Length > 4 && int.TryParse(parts[4], out var c) ? c : 0;
        double value; string unit = SensorReading.DefaultUnit(kind); var ts = DateTimeOffset.UtcNow;
        payload = payload.Trim();
        if (payload.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var r = doc.RootElement;
                if (!r.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number) return false;
                value = v.GetDouble();
                if (r.TryGetProperty("u", out var u) && u.GetString() is { } us) unit = us;
                if (r.TryGetProperty("ts", out var t) && t.TryGetDateTimeOffset(out var tv)) ts = tv;
            }
            catch (JsonException) { return false; }
        }
        else if (!double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        reading = new SensorReading(parts[2], kind, value, unit, ts, ch);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
