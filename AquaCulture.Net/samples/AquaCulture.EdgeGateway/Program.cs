// aqua-edge: NativeAOT edge gateway for ponds. Made by Gravicode Studios, led by Kang Fadhil.
//
//   aqua-edge --selftest                                           48 simulated hours in a few seconds, exit code 0 = healthy
//   aqua-edge --simulate --ponds 3                                 live loop on simulated probes
//   aqua-edge --modbus 192.168.1.50:502 --pond P-01 --device probe-1 --aerator aer-1
//   ... --cloud https://cloud.example.com --tenant koperasi-a --gateway gw-01     (API key in AQUA_SYNC_KEY)
//
// The gateway decides locally (alerts, aerators) and keeps working offline; readings wait in the outbox (--data, default ./aqua-edge-data)
// until the cloud acknowledges them.
using System.Globalization;
using System.Text.Json.Nodes;
using AquaCulture.Core;
using AquaCulture.EdgeGateway;
using AquaCulture.IoT;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var opt = Options.Parse(args);
if (opt.Has("help") || opt.Has("h")) { Console.WriteLine(Options.Usage); return 0; }
Console.WriteLine($"aqua-edge {AquaInfo.Version} — {AquaInfo.CreditEn}");
if (opt.Has("selftest"))
    return await SelfTest.RunAsync(opt.Get("data") ?? Path.Combine(Path.GetTempPath(), "aqua-edge-selftest-" + Environment.ProcessId), opt.Has("require-native"));

string dataDir = opt.Get("data") ?? "aqua-edge-data";
using var outbox = new EdgeOutbox(Path.Combine(dataDir, "outbox"));
string settingsPath = Path.Combine(dataDir, "settings.json");
var settings = LoadSettings(settingsPath, opt.Get("species"));
int interval = int.Parse(opt.Get("interval") ?? "60", CultureInfo.InvariantCulture);

// ── sensor source and actuators ──
Func<DateTime, CancellationToken, Task<IReadOnlyList<SensorReading>>> read;
ISensorTransport actuators;
var aerators = new Dictionary<string, string>();
if (opt.Get("modbus") is { } modbus)
{
    var (host, port) = modbus.Split(':') is [var h, var p] ? (h, int.Parse(p, CultureInfo.InvariantCulture)) : (modbus, 502);
    string pond = opt.Get("pond") ?? "P-01", device = opt.Get("device") ?? "probe-1";
    var client = new ModbusTcpClient(host, port);
    read = async (_, ct) => await client.ReadProbeAsync(device, pond, Modbus.TypicalProbeMap, ct);
    actuators = new LoggingActuators();
    if (opt.Get("aerator") is { } a) aerators[pond] = a;
}
else
{
    int n = int.Parse(opt.Get("ponds") ?? "3", CultureInfo.InvariantCulture);
    var ponds = Enumerable.Range(1, n).Select(i => new SimulatedPond($"P-{i:00}", $"probe-{i}", MeanDo: 5.2 - 0.3 * i, SalinityPpt: 20)).ToList();
    var sim = new SimulatedSensorTransport(ponds);
    foreach (var p in ponds) aerators[p.PondId] = "aer-" + p.PondId;
    read = (t, _) => Task.FromResult(sim.Emit(t));
    actuators = sim;
}
await actuators.ConnectAsync();
var core = new GatewayCore(actuators, outbox, aerators) { Settings = settings };

// ── cloud sync (optional) ──
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Task syncTask = Task.CompletedTask;
if (opt.Get("cloud") is { } cloud)
{
    string key = Environment.GetEnvironmentVariable("AQUA_SYNC_KEY") ?? throw new InvalidOperationException("Set AQUA_SYNC_KEY to the tenant API key.");
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var sync = new CloudSyncClient(http, new Uri(cloud.TrimEnd('/') + "/"), opt.Get("tenant") ?? throw new ArgumentException("--tenant is required with --cloud"), key,
        opt.Get("gateway") ?? Environment.MachineName.ToLowerInvariant());
    sync.ConfigChanged += cfg =>
    {
        core.Settings = core.Settings.Apply(cfg);
        File.WriteAllText(settingsPath, cfg.Values.ToJsonString());
        Console.WriteLine($"config {cfg.Version}: DO set point {core.Settings.DoSetPoint.ToString(CultureInfo.InvariantCulture)} mg/L, species {core.Settings.Species}");
    };
    syncTask = sync.RunAsync(outbox, TimeSpan.FromSeconds(Math.Max(10, interval)), r =>
        Console.WriteLine(r.Error is null ? $"sync: sent {r.Sent}, pending {r.Pending}" : $"sync offline ({r.Error}); pending {r.Pending}, retrying"), cts.Token);
}

Console.WriteLine($"gateway running: {aerators.Count} aerator(s), DO set point {settings.DoSetPoint.ToString(CultureInfo.InvariantCulture)} mg/L, outbox {outbox.Pending} pending. Ctrl+C to stop.");
while (!cts.IsCancellationRequested)
{
    var now = DateTime.UtcNow;
    try
    {
        var report = await core.TickAsync(await read(now, cts.Token), now, cts.Token);
        foreach (var a in report.Alerts) Console.WriteLine($"[{a.Severity}] {a.PondId}: {a.Message.En}");
        foreach (var c in report.Commands) Console.WriteLine($"→ {c.DeviceId} {c.Action}");
    }
    catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or TimeoutException or InvalidDataException)
    {
        Console.WriteLine($"sensor read failed: {e.Message}");                     // keep running; the next tick retries
    }
    try { await Task.Delay(TimeSpan.FromSeconds(interval), cts.Token); } catch (OperationCanceledException) { }
}
await syncTask;
Console.WriteLine($"stopped; {outbox.Pending} reading(s) wait in the outbox.");
return 0;

static GatewaySettings LoadSettings(string path, string? species)
{
    var s = new GatewaySettings(Species: species ?? "vaname");
    if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) s = s.Apply(new GatewayConfig("disk", o));
    return s;
}

namespace AquaCulture.EdgeGateway
{
    /// <summary>Actuators that only log (replace with a relay board or a Modbus coil write).</summary>
    internal sealed class LoggingActuators : ISensorTransport
    {
        public event Action<SensorReading>? ReadingReceived { add { } remove { } }
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default) { Console.WriteLine("actuator: " + command.ToJson()); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class Options
    {
        private readonly Dictionary<string, string?> _v = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-')) continue;
                string k = args[i].TrimStart('-');
                o._v[k] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            }
            return o;
        }

        public bool Has(string k) => _v.ContainsKey(k);
        public string? Get(string k) => _v.TryGetValue(k, out var v) ? v : null;

        public const string Usage = """
            aqua-edge — AquaCulture.Net edge gateway (NativeAOT)
              --selftest [--require-native]    simulate 48 h and verify the pipeline (exit code 0 = healthy)
              --simulate [--ponds N]           simulated probes (default when --modbus is not given)
              --modbus host[:port] --pond P --device D [--aerator A]
              --cloud URL --tenant T [--gateway G]   offline-first sync (API key in AQUA_SYNC_KEY)
              --data DIR                       outbox and settings (default ./aqua-edge-data)
              --species ID                     species profile (default vaname)
              --interval S                     seconds between readings (default 60)
            """;
    }
}
