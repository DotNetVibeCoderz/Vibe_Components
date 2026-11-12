using System.Net.Http.Json;
using System.Text;
using AquaCulture.Core.Data;
using MQTTnet;

namespace AquaCulture.IoT;

/// <summary>MQTT connection settings.</summary>
public sealed record MqttOptions(string Host = "localhost", int Port = 1883, string TopicRoot = "aquaculture", string FarmId = "farm1",
    string? Username = null, string? Password = null, bool UseTls = false, string? ClientId = null);

/// <summary>
/// MQTT transport (MQTTnet 5). Telemetry topic: <c>{root}/{farm}/{pond}/{device}/telemetry</c> with a multi-value JSON payload
/// (see <see cref="SensorReading.ParseTelemetry"/>); commands are published to <c>{root}/{farm}/cmd/{device}</c>.
/// </summary>
public sealed class MqttSensorTransport(MqttOptions options) : ISensorTransport
{
    private IMqttClient? _client;

    public event Action<SensorReading>? ReadingReceived;
    public bool IsConnected => _client?.IsConnected == true;

    public static string TelemetryTopic(MqttOptions o, string pond, string device) => $"{o.TopicRoot}/{o.FarmId}/{pond}/{device}/telemetry";
    public static string CommandTopic(MqttOptions o, string device) => $"{o.TopicRoot}/{o.FarmId}/cmd/{device}";

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client = new MqttClientFactory().CreateMqttClient();
        var b = new MqttClientOptionsBuilder().WithTcpServer(options.Host, options.Port)
            .WithClientId(options.ClientId ?? $"aqua-{Guid.NewGuid():N}"[..20]).WithCleanSession();
        if (options.Username is not null) b = b.WithCredentials(options.Username, options.Password);
        if (options.UseTls) b = b.WithTlsOptions(o => o.UseTls());
        _client.ApplicationMessageReceivedAsync += e =>
        {
            foreach (var r in Parse(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString())) ReadingReceived?.Invoke(r);
            return Task.CompletedTask;
        };
        await _client.ConnectAsync(b.Build(), ct).ConfigureAwait(false);
        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter($"{options.TopicRoot}/{options.FarmId}/+/+/telemetry").Build(), ct).ConfigureAwait(false);
    }

    /// <summary>Parses a telemetry topic and payload (pond and device come from the topic when absent in the JSON).</summary>
    public static IReadOnlyList<SensorReading> Parse(string topic, string payload)
    {
        var parts = topic.Split('/');
        if (parts.Length < 5 || parts[^1] != "telemetry") return [];
        try { return SensorReading.ParseTelemetry(payload, parts[^2], parts[^3]); }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public async Task PublishTelemetryAsync(string pond, string device, string json, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Call ConnectAsync first.");
        await _client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(TelemetryTopic(options, pond, device)).WithPayload(json).Build(), ct).ConfigureAwait(false);
    }

    public async Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Call ConnectAsync first.");
        await _client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(CommandTopic(options, command.DeviceId)).WithPayload(command.ToJson()).Build(), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}

/// <summary>Polls a REST endpoint that returns telemetry JSON (an object or an array of objects); commands are POSTed as JSON.</summary>
public sealed class RestSensorTransport(Uri telemetryUrl, Uri? commandUrl = null, TimeSpan? interval = null, HttpClient? http = null) : ISensorTransport
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<SensorReading>? ReadingReceived;
    public bool IsConnected => _loop is { IsCompleted: false };

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        string json = await _http.GetStringAsync(telemetryUrl, ct).ConfigureAwait(false);
        int n = 0;
        foreach (var r in ParseDocument(json)) { ReadingReceived?.Invoke(r); n++; }
        return n;
    }

    public static IEnumerable<SensorReading> ParseDocument(string json)
    {
        var t = json.TrimStart();
        if (!t.StartsWith('[')) return SensorReading.ParseTelemetry(json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().SelectMany(e => SensorReading.ParseTelemetry(e.GetRawText())).ToList();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(30));
        do
        {
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    public async Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default)
    {
        if (commandUrl is null) throw new InvalidOperationException("No command URL configured.");
        using var content = new StringContent(command.ToJson(), Encoding.UTF8, "application/json");
        (await _http.PostAsync(commandUrl, content, ct).ConfigureAwait(false)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) { await _cts.CancelAsync().ConfigureAwait(false); try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _cts?.Dispose();
    }
}

/// <summary>Per-pond simulated sensor profile.</summary>
public sealed record SimulatedPond(string PondId, string DeviceId, double MeanTempC = 29, double MeanDo = 5.5, double DoAmplitude = 2.2, double Ph = 7.8,
    double SalinityPpt = 0, double Tan = 0.6, double Turbidity = 30, double Orp = 260);

/// <summary>
/// Deterministic simulated sensor network (diurnal DO and temperature, pH following photosynthesis, slow TAN drift, noise and
/// occasional dropouts). Use it for demos, tests and twin development without hardware.
/// </summary>
public sealed class SimulatedSensorTransport(IReadOnlyList<SimulatedPond> ponds, ulong seed = 7) : ISensorTransport
{
    private readonly DeterministicRandom _rng = new(seed);
    private readonly List<ActuatorCommand> _commands = [];
    private readonly Dictionary<string, bool> _aerators = [];

    public event Action<SensorReading>? ReadingReceived;
    public bool IsConnected { get; private set; }
    public IReadOnlyList<ActuatorCommand> Commands => _commands;
    public double DropoutProbability { get; init; } = 0.02;

    public Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }

    /// <summary>Produces the readings of every pond at a time (and raises <see cref="ReadingReceived"/>).</summary>
    public IReadOnlyList<SensorReading> Emit(DateTime time)
    {
        var list = new List<SensorReading>();
        double hour = time.Hour + time.Minute / 60.0;
        foreach (var p in ponds)
        {
            if (_rng.NextDouble() < DropoutProbability) continue;
            bool aer = _aerators.GetValueOrDefault(p.DeviceId);
            double diurnal = Math.Sin(2 * Math.PI * (hour - 9) / 24);         // max ≈ 15:00, min ≈ 03:00
            double temp = p.MeanTempC + 1.6 * Math.Sin(2 * Math.PI * (hour - 10) / 24) + 0.1 * _rng.NextGaussian();
            double dox = Math.Max(0.2, p.MeanDo + p.DoAmplitude * diurnal + (aer ? 1.2 : 0) + 0.15 * _rng.NextGaussian());
            double ph = p.Ph + 0.35 * diurnal + 0.03 * _rng.NextGaussian();
            double tan = Math.Max(0, p.Tan * (1 + 0.1 * Math.Sin(2 * Math.PI * time.DayOfYear / 7.0)) + 0.03 * _rng.NextGaussian());
            void Add(SensorKind k, double v) => list.Add(new SensorReading(p.DeviceId, p.PondId, k, Math.Round(v, 3), time, SensorReading.DefaultUnit(k)));
            Add(SensorKind.Temperature, temp);
            Add(SensorKind.DissolvedOxygen, dox);
            Add(SensorKind.Ph, ph);
            Add(SensorKind.Ammonia, tan);
            Add(SensorKind.Orp, p.Orp + 20 * diurnal + 5 * _rng.NextGaussian());
            Add(SensorKind.Turbidity, p.Turbidity + 3 * _rng.NextGaussian());
            if (p.SalinityPpt > 0) Add(SensorKind.Salinity, p.SalinityPpt + 0.1 * _rng.NextGaussian());
            Add(SensorKind.Battery, 3.9 - 0.0001 * time.DayOfYear + 0.01 * _rng.NextGaussian());
        }
        foreach (var r in list) ReadingReceived?.Invoke(r);
        return list;
    }

    public Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default)
    {
        _commands.Add(command);
        if (command.Action is "aerator_on" or "aerator_off") _aerators[command.DeviceId] = command.Action == "aerator_on";
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
}
