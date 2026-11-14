using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IoTCom.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Serialization.SenML;
using IoTCom.Net.Transports;
using Medical.Hospital.Monitoring;

namespace Medical.Devices;

/// <summary>LOINC / SenML names of the parameters bedside devices report.</summary>
public static class DeviceCodes
{
    /// <summary>LOINC codes used by monitors in ORU^R01 (plus IEEE 11073-mapped alternatives some vendors send).</summary>
    public static IReadOnlyDictionary<string, VitalParameter> Loinc { get; } = new Dictionary<string, VitalParameter>
    {
        ["8867-4"] = VitalParameter.HeartRate, ["9279-1"] = VitalParameter.RespiratoryRate, ["59408-5"] = VitalParameter.SpO2, ["2708-6"] = VitalParameter.SpO2,
        ["8480-6"] = VitalParameter.SystolicBp, ["8462-4"] = VitalParameter.DiastolicBp, ["8310-5"] = VitalParameter.TemperatureC, ["8331-1"] = VitalParameter.TemperatureC,
    };

    /// <summary>SenML record names (lower-case) for MQTT sensors.</summary>
    public static IReadOnlyDictionary<string, VitalParameter> SenML { get; } = new Dictionary<string, VitalParameter>
    {
        ["hr"] = VitalParameter.HeartRate, ["rr"] = VitalParameter.RespiratoryRate, ["spo2"] = VitalParameter.SpO2, ["sbp"] = VitalParameter.SystolicBp,
        ["dbp"] = VitalParameter.DiastolicBp, ["temp"] = VitalParameter.TemperatureC, ["o2"] = VitalParameter.OnOxygen,
    };
}

/// <summary>
/// Receives HL7 v2 ORU^R01 from patient monitors over MLLP (IoTCom.Net) and turns OBX vital signs into
/// <see cref="DeviceReading"/>s. The device id is the sending application (MSH-3) unless <c>deviceId</c> maps the bed (PV1-3)
/// or another field. Listens on TCP, or in memory for tests and demos.
/// </summary>
public sealed class Hl7MonitorFeed : IDeviceFeed, IAsyncDisposable
{
    private readonly Hl7MllpServer _server;
    private readonly Channel<DeviceReading> _out = Channel.CreateUnbounded<DeviceReading>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<Hl7Message, string> _deviceId;
    private int _started;

    public string Name { get; }
    public long MessagesReceived => _server.MessagesReceived;
    public int Connections => _server.ConnectionCount;
    public InMemoryTransportListener? InMemory { get; }

    private Hl7MonitorFeed(string name, Action<Hl7MllpServerOptions> configure, Func<Hl7Message, string>? deviceId, InMemoryTransportListener? inMemory)
    {
        Name = name;
        InMemory = inMemory;
        _deviceId = deviceId ?? (m => m.Sender.Item1);
        _server = Hl7MllpServer.Create(configure);
        _server.MessageReceived += (_, e) => OnMessage(e.Message);
    }

    /// <summary>An MLLP listener on a TCP port (monitor gateways usually send to 2575 or a vendor port).</summary>
    public static Hl7MonitorFeed Tcp(int port, Func<Hl7Message, string>? deviceId = null) =>
        new($"HL7/MLLP :{port}", o => o.UseTcp(IPAddress.Any, port), deviceId, null);

    /// <summary>An in-process listener; connect simulators with <see cref="InMemory"/>.</summary>
    public static Hl7MonitorFeed InProcess(string name = "hl7-monitors", Func<Hl7Message, string>? deviceId = null)
    {
        var listener = new InMemoryTransportListener(name);
        return new($"HL7/MLLP in-process ({name})", o => o.ListenInMemory(listener), deviceId, listener);
    }

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) await _server.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Readings in a parsed ORU (exposed for testing and for feeds that receive HL7 another way).</summary>
    public static IEnumerable<DeviceReading> Readings(Hl7Message m, string deviceId)
    {
        if (!m.MessageType.StartsWith("ORU", StringComparison.Ordinal)) yield break;
        var fallback = m.Timestamp ?? DateTimeOffset.UtcNow;
        foreach (var obs in m.GetObservations())
        {
            if (obs.NumericValue is not { } value || !DeviceCodes.Loinc.TryGetValue(obs.Code.Identifier, out var p)) continue;
            if (p == VitalParameter.TemperatureC && obs.Units is "[degF]" or "degF") value = Math.Round((value - 32) / 1.8, 1);
            yield return new DeviceReading(deviceId, p, value, obs.Timestamp ?? fallback);
        }
    }

    private void OnMessage(Hl7Message m)
    {
        foreach (var r in Readings(m, _deviceId(m))) _out.Writer.TryWrite(r);
    }

    public async IAsyncEnumerable<DeviceReading> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        await foreach (var r in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return r;
    }

    public async ValueTask DisposeAsync()
    {
        _out.Writer.TryComplete();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Infusion-pump telemetry (rate, volume to be infused, alarm), as published by smart pumps over MQTT / SenML.</summary>
public sealed record InfusionPumpStatus(string DeviceId, string? Drug, double RateMlPerHour, double VolumeInfusedMl, double VolumeToBeInfusedMl, string? Alarm, DateTimeOffset Time)
{
    /// <summary>Minutes until the bag is empty at the current rate.</summary>
    public double? MinutesRemaining => RateMlPerHour > 0 ? Math.Round((VolumeToBeInfusedMl - VolumeInfusedMl) / RateMlPerHour * 60, 0) : null;
}

/// <summary>
/// Subscribes to SenML packs over MQTT (IoTCom.Net): vital-sign sensors (<c>hr, rr, spo2, sbp, dbp, temp, o2</c>) become
/// <see cref="DeviceReading"/>s, and infusion pumps (<c>rate, vi, vtbi, alarm, drug</c>) raise <see cref="PumpStatus"/>. The
/// device id is the SenML base name without its trailing separator, or the topic's second segment.
/// </summary>
public sealed class MqttSenMLFeed(MqttEndpoint endpoint, string topicFilter = "medical/+/telemetry") : IDeviceFeed, IAsyncDisposable
{
    public string Name => $"MQTT SenML {topicFilter}";

    public event Action<InfusionPumpStatus>? PumpStatus;

    public static MqttSenMLFeed Connect(string host, int port, string topicFilter = "medical/+/telemetry", string clientId = "medical-net-gateway") =>
        new(MqttEndpoint.Create(o => o.UseBroker(host, port).WithClientId(clientId)), topicFilter);

    public async IAsyncEnumerable<DeviceReading> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!endpoint.IsConnected) await endpoint.ConnectAsync(ct).ConfigureAwait(false);
        await foreach (var msg in endpoint.SubscribeAsync(topicFilter, ct).ConfigureAwait(false))
        {
            IReadOnlyList<SenMLResolvedRecord> recs;
            try { recs = SenMLCodec.Resolve(SenMLCodec.ParseJson(msg.Payload.Span), msg.Timestamp); }
            catch (Exception) { continue; }   // malformed pack: skip, the next one will come
            foreach (var r in Readings(recs, msg.Topic)) yield return r;
        }
    }

    /// <summary>Splits resolved SenML records into vital-sign readings and pump status (exposed for testing).</summary>
    public IEnumerable<DeviceReading> Readings(IReadOnlyList<SenMLResolvedRecord> recs, string topic)
    {
        var pump = new Dictionary<string, SenMLResolvedRecord>(StringComparer.OrdinalIgnoreCase);
        string? device = null;
        DateTimeOffset time = DateTimeOffset.UtcNow;
        foreach (var r in recs)
        {
            int sep = Math.Max(r.Name.LastIndexOf(':'), r.Name.LastIndexOf('/'));
            string dev = sep > 0 ? r.Name[..sep] : topic.Split('/') is { Length: > 1 } parts ? parts[1] : topic;
            string field = (sep > 0 ? r.Name[(sep + 1)..] : r.Name).ToLowerInvariant();
            device = dev;
            time = r.Time;
            if (DeviceCodes.SenML.TryGetValue(field, out var p))
            {
                double v = p == VitalParameter.OnOxygen ? (r.BoolValue == true || r.Value >= 0.5 ? 1 : 0) : r.Value ?? double.NaN;
                if (!double.IsNaN(v)) yield return new DeviceReading(dev, p, v, r.Time);
            }
            else pump[field] = r;
        }
        if (device is not null && pump.ContainsKey("rate"))
            PumpStatus?.Invoke(new InfusionPumpStatus(device, pump.GetValueOrDefault("drug")?.StringValue, pump["rate"].Value ?? 0,
                pump.GetValueOrDefault("vi")?.Value ?? 0, pump.GetValueOrDefault("vtbi")?.Value ?? 0, pump.GetValueOrDefault("alarm")?.StringValue, time));
    }

    public async ValueTask DisposeAsync()
    {
        if (endpoint.IsConnected) await endpoint.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        await endpoint.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A simulated bedside monitor bound to a device id (wraps the IoTCom.Net patient-monitor simulator).</summary>
public sealed record SimulatedMonitor(string DeviceId, PatientMonitorSimulator Simulator);

/// <summary>
/// Drives simulated monitors (IoTCom.Net <see cref="PatientMonitorSimulator"/>: stable, sepsis, hypoxia, hypertension) and
/// sends their ORU^R01 over MLLP to an <see cref="Hl7MonitorFeed"/>, exactly as real monitors would. Simulated time can
/// run faster than real time for demos.
/// </summary>
public sealed class BedsideSimulation : IAsyncDisposable
{
    private readonly List<(SimulatedMonitor Monitor, Hl7MllpClient Client)> _monitors = [];

    public IReadOnlyList<SimulatedMonitor> Monitors => _monitors.Select(m => m.Monitor).ToList();

    /// <summary>Connects one MLLP client per monitor to an in-process feed. Device ids are written to MSH-3.</summary>
    public static async Task<BedsideSimulation> StartAsync(Hl7MonitorFeed feed, IEnumerable<(string DeviceId, string Bed, PatientScenario Scenario)> beds, int seed = 7, CancellationToken ct = default)
    {
        if (feed.InMemory is null) throw new ArgumentException("Use an in-process feed (Hl7MonitorFeed.InProcess) or connect clients over TCP yourself.", nameof(feed));
        await feed.StartAsync(ct).ConfigureAwait(false);
        var sim = new BedsideSimulation();
        int i = 0;
        foreach (var (deviceId, bed, scenario) in beds)
        {
            var (patient, _, _) = PatientMonitorSimulator.DemoWard[i % PatientMonitorSimulator.DemoWard.Count];
            var monitor = new SimulatedMonitor(deviceId, new PatientMonitorSimulator(patient, scenario, bed, seed + i, TimeSpan.FromMinutes(20)));
            var client = Hl7MllpClient.Create(o => o.UseInMemory(feed.InMemory));
            await client.ConnectAsync(ct).ConfigureAwait(false);
            sim._monitors.Add((monitor, client));
            i++;
        }
        return sim;
    }

    /// <summary>Advances every monitor by <paramref name="step"/> of simulated time and sends one ORU each.</summary>
    public async Task<IReadOnlyList<VitalsSample>> TickAsync(TimeSpan step, DateTimeOffset now, CancellationToken ct = default)
    {
        var samples = new List<VitalsSample>();
        foreach (var (m, client) in _monitors)
        {
            var sample = m.Simulator.Next(step, now);
            var oru = m.Simulator.ToOru(sample);
            var text = oru.Encode();
            var withDevice = Hl7Message.Parse(ReplaceSendingApp(text, m.DeviceId));
            await client.SendAsync(withDevice, true, ct).ConfigureAwait(false);
            samples.Add(sample);
        }
        return samples;
    }

    private static string ReplaceSendingApp(string er7, string app)
    {
        var lines = er7.Split('\r');
        var f = lines[0].Split('|');
        if (f.Length > 2) f[2] = app;
        lines[0] = string.Join('|', f);
        return string.Join('\r', lines);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, c) in _monitors) await c.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Publishes simulated infusion-pump telemetry as SenML JSON over MQTT.</summary>
public sealed class InfusionPumpSimulator(MqttEndpoint endpoint, string deviceId, string drug, double rateMlPerHour, double vtbiMl)
{
    public double VolumeInfused { get; private set; }

    public async Task PublishAsync(TimeSpan elapsed, DateTimeOffset now, string? alarm = null, CancellationToken ct = default)
    {
        VolumeInfused = Math.Min(vtbiMl, VolumeInfused + rateMlPerHour * elapsed.TotalHours);
        alarm ??= VolumeInfused >= vtbiMl ? "VTBI_DONE" : vtbiMl - VolumeInfused < rateMlPerHour / 6 ? "NEAR_EMPTY" : null;
        var b = new SenMLPackBuilder($"{deviceId}:").At(now).Add("rate", rateMlPerHour, "mL/h", null).Add("vi", Math.Round(VolumeInfused, 1), "mL", null)
            .Add("vtbi", vtbiMl, "mL", null).Add("drug", drug);
        if (alarm is not null) b.Add("alarm", alarm);
        if (!endpoint.IsConnected) await endpoint.ConnectAsync(ct).ConfigureAwait(false);
        await endpoint.PublishAsync($"medical/{deviceId}/telemetry", SenMLCodec.ToJson(b.Build()), new PublishOptions { ContentType = SenMLCodec.JsonContentType }, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Pumps readings from any number of <see cref="IDeviceFeed"/>s into a <see cref="MonitoringEngine"/> until cancelled.
/// One failing feed does not stop the others; its error is reported through <see cref="FeedError"/>.
/// </summary>
public sealed class DeviceGateway(MonitoringEngine engine, params IDeviceFeed[] feeds)
{
    public event Action<IDeviceFeed, Exception>? FeedError;
    public long Readings => Interlocked.Read(ref _count);
    private long _count;

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(feeds.Select(f => PumpAsync(f, ct)));

    private async Task PumpAsync(IDeviceFeed feed, CancellationToken ct)
    {
        try
        {
            await foreach (var r in feed.ReadAllAsync(ct).ConfigureAwait(false))
            {
                engine.Ingest(r);
                Interlocked.Increment(ref _count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { FeedError?.Invoke(feed, e); }
    }

    /// <summary>Formats a reading for logs.</summary>
    public static string Describe(DeviceReading r) => string.Create(CultureInfo.InvariantCulture, $"{r.DeviceId} {r.Parameter}={r.Value:0.#} @{r.Time:HH:mm:ss}");
}
