using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.SCADA.Modbus;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;

namespace Electrical.Gallery.Shared;

/// <summary>One RTU point as read back from the register map.</summary>
public sealed record ScadaPointRow(string Name, string Table, ushort Address, string Registers, double Value, double TrueValue, string Unit);

/// <summary>Alarm summary row.</summary>
public sealed record ScadaAlarmRow(string Tag, AlarmState State, AlarmPriority Priority, string? Limit, double Value, DateTimeOffset? RaisedAt);

/// <summary>Alarm journal entry.</summary>
public sealed record ScadaEventRow(DateTimeOffset Time, string Tag, AlarmState From, AlarmState To, AlarmPriority Priority, string Message);

public sealed record ScadaDemoResult(
    double PeakScale, double Deviation, IReadOnlyList<ScadaPointRow> Points, IReadOnlyList<ScadaAlarmRow> Alarms, IReadOnlyList<ScadaEventRow> Events,
    string HistorianSvg, string VoltageTrendSvg, long RawSamples, int ArchivedSamples, double CompressionRatio,
    string ModbusRequestHex, string ModbusResponseHex, IReadOnlyList<Kpi> Kpis);

/// <summary>
/// A simulated day on IEEE 14-bus: every 5 minutes the load follows a daily curve, the power flow is re-solved and published by an
/// <see cref="RtuSimulator"/>; a <see cref="TagDatabase"/> scans the register map (in-process, no sockets), feeding an
/// <see cref="AlarmManager"/> and a swinging-door <see cref="Historian"/>. The operator acknowledges all alarms at 12:00.
/// </summary>
public static class ScadaScenario
{
    public const string HistorianTag = "BR0.PF";
    public const string VoltageTag = "BUS14.VM";
    private static readonly DateTimeOffset Day = new(2026, 11, 14, 0, 0, 0, TimeSpan.FromHours(7));

    /// <summary>Daily load shape (Java–Bali style evening peak), 0..1.</summary>
    public static double Shape(double hour) =>
        0.62 + 0.14 * Math.Exp(-Math.Pow((hour - 10.5) / 3.2, 2)) + 0.38 * Math.Exp(-Math.Pow((hour - 19.2) / 2.1, 2)) - 0.06 * Math.Exp(-Math.Pow((hour - 3.5) / 2.5, 2));

    public static ScadaDemoResult Run(double peakScale = 1.3, double deviationMw = 0.5, string lang = "en")
    {
        var baseNet = TestCases.Case14();
        var net = baseNet.Clone();
        var first = PowerFlowSolver.Solve(net);
        var rtu = new RtuSimulator(first, new RtuOptions { UnitId = 1, PowerNoiseMw = 0.08 });
        var tags = new TagDatabase();
        rtu.RegisterTags(tags);

        var alarms = new AlarmManager();
        var events = new List<ScadaEventRow>();
        alarms.AlarmChanged += e => events.Add(new ScadaEventRow(e.Timestamp, e.Tag, e.PreviousState, e.State, e.Priority, e.Message));
        foreach (var b in net.Buses.Where(b => b.Type == BusType.PQ))
            alarms.Define(new AlarmDefinition($"BUS{b.Id}.VM", HighHigh: 1.08, High: 1.07, Low: 1.02, LowLow: 1.005, Deadband: 0.002, Priority: AlarmPriority.Medium,
                Description: $"Bus {b.Id} voltage"));
        alarms.Define(new AlarmDefinition("BR0.PF", HighHigh: 240, High: 200, Deadband: 3, Priority: AlarmPriority.High, Description: "Line 1–2 flow"));
        alarms.Define(new AlarmDefinition("BR1.PF", HighHigh: 115, High: 95, Deadband: 2, Priority: AlarmPriority.Medium, Description: "Line 1–5 flow"));
        alarms.Define(new AlarmDefinition("GEN0.Q", Low: -22, Deadband: 1, Priority: AlarmPriority.Low, Description: "G1 reactive absorption"));
        using var a1 = alarms.Attach(tags);

        var historian = new Historian(new HistorianTagOptions(CompressionMode.None));
        historian.Configure(HistorianTag, new HistorianTagOptions(CompressionMode.SwingingDoor, deviationMw, TimeSpan.FromHours(2)));
        historian.Configure(VoltageTag, new HistorianTagOptions(CompressionMode.SwingingDoor, 0.0015));
        using var h1 = historian.Attach(tags);

        var raw = new List<double>();
        var step = TimeSpan.FromMinutes(5);
        int steps = 288;
        bool acked = false;
        PowerFlowResult r = first;
        for (int k = 0; k < steps; k++)
        {
            var t = Day + step * k;
            double f = 0.55 + (peakScale - 0.55) * (Shape(t.TimeOfDay.TotalHours) - 0.56) / 0.44;
            for (int i = 0; i < net.Loads.Count; i++) { net.Loads[i].P = baseNet.Loads[i].P * f; net.Loads[i].Q = baseNet.Loads[i].Q * f; }
            for (int i = 0; i < net.Generators.Count; i++) if (net.GetBus(net.Generators[i].Bus).Type != BusType.Slack) net.Generators[i].P = baseNet.Generators[i].P * f;
            r = PowerFlowSolver.Solve(net, new PowerFlowOptions { FlatStart = false });
            r.ApplyTo(net);
            rtu.Publish(r);
            tags.Scan(rtu.Map, 1, t);
            raw.Add(tags[HistorianTag].Value);
            if (!acked && t.TimeOfDay >= TimeSpan.FromHours(12)) { alarms.AcknowledgeAll("operator", t); acked = true; }
        }

        // Historian chart: raw scan vs archived (interpolated between stored points).
        var archived = historian.Query(HistorianTag, Day, Day + step * steps);
        var archX = archived.Select(s => (s.Timestamp - Day) / step).ToList();
        string hsvg = SvgCharts.Line(
        [
            new ChartSeries(lang == "id" ? "pindaian mentah" : "raw scans", raw, SvgTheme.Var("eln-line")) { StrokeWidth = 4 },
            new ChartSeries(lang == "id" ? $"arsip ({archived.Count} titik)" : $"archived ({archived.Count} points)", archived.Select(s => s.Value).ToList(), SvgTheme.Var("eln-accent"))
                { X = archX, StrokeWidth = 1.6 },
        ], new ChartOptions { Width = 680, Height = 260, YLabel = "BR0.PF (MW)", TimeStart = Day, TimeStep = step });

        var vts = historian.Resample(VoltageTag, Day, Day + step * (steps - 1), TimeSpan.FromMinutes(15), "pu");
        string vsvg = SvgCharts.TimeSeries([vts], new ChartOptions
        {
            Width = 680, Height = 220, YLabel = "BUS14.VM (pu)", ShowLegend = false, ReferenceLines = [(1.02, "L"), (1.005, "LL")],
        });

        // Read back the final point list through the register map.
        var points = rtu.Points.Where(p => p.Name.StartsWith("BUS", StringComparison.Ordinal) || p.Name.StartsWith("GEN", StringComparison.Ordinal) || p.Name is "BR0.PF" or "BR0.QF" or "BR1.PF" or "BR0.CLOSED")
            .Select(p =>
            {
                string regs = p.DataType == TagDataType.Bool ? (rtu.Map.GetBit(p.Table, p.Address) ? "1" : "0")
                    : string.Join(' ', rtu.Map.ReadRegisters(p.Table, p.Address, 2).Select(x => x.ToString("X4")));
                double v = p.DataType == TagDataType.Bool ? (rtu.Map.GetBit(p.Table, p.Address) ? 1 : 0) : rtu.Map.GetFloat(p.Table, p.Address);
                return new ScadaPointRow(p.Name, p.Table.ToString(), p.Address, regs, v, p.TrueValue, p.Unit);
            }).ToList();

        var alarmRows = alarms.All.Where(a => a.State != AlarmState.Normal || a.RaisedAt is not null)
            .OrderByDescending(a => a.State is AlarmState.Unacknowledged ? 2 : a.State is AlarmState.Acknowledged or AlarmState.ReturnToNormalUnacknowledged ? 1 : 0)
            .ThenByDescending(a => a.Priority)
            .Select(a => new ScadaAlarmRow(a.Tag, a.State, a.Priority, a.LastLimit?.ToString(), a.LastValue, a.RaisedAt)).ToList();

        // Modbus PDU / ADU for reading bus 14 (Vm, Va, kV = 6 registers) – encoded and served by the in-process map.
        int bus14 = net.IndexOf(14);
        var req = ModbusRequest.ReadInputRegisters(rtu.BusAddress(bus14), 6);
        var reqAdu = ModbusTcpAdu.Encode(0x002A, 1, ModbusPdu.EncodeRequest(req));
        var resp = rtu.Map.Process(req);
        var respAdu = ModbusTcpAdu.Encode(0x002A, 1, ModbusPdu.EncodeResponse(resp));

        long rawCount = historian.ReceivedCount(HistorianTag);
        int arch = historian.ArchivedCount(HistorianTag);
        int unack = alarms.All.Count(a => a.State is AlarmState.Unacknowledged or AlarmState.ReturnToNormalUnacknowledged);
        int active = alarms.ActiveAlarms.Count;
        var kpis = new List<Kpi>
        {
            new(new("Tags scanned", "Tag dipindai"), tags.Count.ToString(), null, new($"{steps} scans · 5 min", $"{steps} pindaian · 5 mnt")),
            new(new("Active alarms", "Alarm aktif"), active.ToString(), null, new($"{unack} unacknowledged", $"{unack} belum diakui"), unack > 0 ? "alarm" : active > 0 ? "warn" : "ok"),
            new(new("Alarm events", "Kejadian alarm"), events.Count.ToString(), null, new("ISA-18.2 transitions", "transisi ISA-18.2")),
            new(new("Compression", "Kompresi"), F(historian.CompressionRatio(HistorianTag), "0.0"), "×", new($"{arch} of {rawCount} samples kept", $"{arch} dari {rawCount} sampel disimpan"), "ok"),
            new(new("Peak flow 1–2", "Aliran puncak 1–2"), F(raw.Max(), "0.0"), "MW", null, raw.Max() > 200 ? "warn" : null),
        };
        return new ScadaDemoResult(peakScale, deviationMw, points, alarmRows, events, hsvg, vsvg, rawCount, arch, historian.CompressionRatio(HistorianTag),
            HexDump.Spaced(reqAdu), HexDump.Spaced(respAdu), kpis);
    }
}
