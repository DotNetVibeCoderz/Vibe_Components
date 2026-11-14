// SCADA edge gateway — built with Electrical.Net (Gravicode Studios, led by Kang Fadhil)
// Polls a Modbus TCP RTU, scales tags, evaluates ISA-18.2 alarms and historizes values.
// Without "--rtu <host> --port <port>" a built-in RTU simulator serving a power-flow result is started.
using System.Net;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.SCADA.Modbus;

string? host = args.SkipWhile(a => a != "--rtu").Skip(1).FirstOrDefault();
int port = int.TryParse(args.SkipWhile(a => a != "--port").Skip(1).FirstOrDefault(), out var p0) ? p0 : 502;

// Simulated RTU (IEEE 14-bus power flow exposed as Float32 input registers)
var rtu = new RtuSimulator(PowerFlowSolver.Solve(TestCases.Case14()));
ModbusTcpServer? simulator = null;
if (host is null)
{
    simulator = new ModbusTcpServer(rtu.Map, IPAddress.Loopback, 0);
    simulator.Start();
    host = "127.0.0.1";
    port = simulator.Port;
    Console.WriteLine($"Started RTU simulator on {host}:{port} with {rtu.Points.Count} points");
}

var tags = new TagDatabase();
var alarms = new AlarmManager();
var historian = new Historian();
foreach (var p in rtu.Points.Where(p => p.Unit == "pu").Take(14))
{
    tags.Add(new Tag(p.Name, p.Unit, new TagAddress(1, p.Table, p.Address, p.DataType)));
    alarms.Define(new AlarmDefinition(p.Name, HighHigh: 1.10, High: 1.06, Low: 0.95, LowLow: 0.90));
}
using var link = alarms.Attach(tags);

await using var client = new ModbusTcpClient();
await client.ConnectAsync(host, port);
for (int scan = 1; scan <= 5; scan++)
{
    rtu.Refresh(); // simulator: new noisy measurements
    await tags.ScanAsync(client);
    foreach (var t in tags.All) historian.Record(t.Name, t.Timestamp, t.Value, t.Quality);
    var active = alarms.ActiveAlarms;
    Console.WriteLine($"Scan {scan}: {tags.Count} tags, {active.Count} active alarms{(active.Count > 0 ? $" — {active[0]}" : "")}");
    await Task.Delay(500);
}
if (simulator is not null) await simulator.StopAsync();
