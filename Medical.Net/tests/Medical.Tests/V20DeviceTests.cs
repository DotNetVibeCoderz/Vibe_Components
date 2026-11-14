using System.Net;
using System.Net.Sockets;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Hl7;
using Medical.Devices;
using Medical.Hospital.Monitoring;

namespace Medical.Tests;

public class DeviceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MonitorOruMapsToReadings()
    {
        var (patient, _, _) = PatientMonitorSimulator.DemoWard[0];
        var sim = new PatientMonitorSimulator(patient, PatientScenario.Stable, "ICU-01", 1, null);
        var sample = sim.Next(TimeSpan.FromMinutes(1), T0);
        var readings = Hl7MonitorFeed.Readings(sim.ToOru(sample), "MON-1").ToDictionary(r => r.Parameter);
        Assert.Equal(sample.HeartRate, readings[VitalParameter.HeartRate].Value, 1);
        Assert.Equal(sample.RespiratoryRate, readings[VitalParameter.RespiratoryRate].Value, 1);
        Assert.Equal(sample.SpO2, readings[VitalParameter.SpO2].Value, 1);
        Assert.Equal(sample.Systolic, readings[VitalParameter.SystolicBp].Value, 1);
        Assert.Equal(sample.Diastolic, readings[VitalParameter.DiastolicBp].Value, 1);
        Assert.Equal(sample.Temperature, readings[VitalParameter.TemperatureC].Value, 1);
        Assert.All(readings.Values, r => Assert.Equal("MON-1", r.DeviceId));
    }

    [Fact]
    public async Task SimulatedMonitorsOverMllpRaiseSepsisAlert()
    {
        await using var feed = Hl7MonitorFeed.InProcess();
        var engine = new MonitoringEngine(new MonitoringOptions { MaxAge = TimeSpan.FromHours(1), TemperatureMaxAge = TimeSpan.FromHours(2) });
        var alerts = new List<EarlyWarningAlert>();
        engine.Alert += a => { lock (alerts) alerts.Add(a); };
        engine.Bind("MON-SEPSIS", "ICU-01", "p1", "Budi Santoso", "v1");
        engine.Bind("MON-STABLE", "ICU-02", "p2", "Sari Wulandari", "v2");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var gateway = new DeviceGateway(engine, feed);
        var run = gateway.RunAsync(cts.Token);
        await using var sim = await BedsideSimulation.StartAsync(feed, [("MON-SEPSIS", "ICU-01", PatientScenario.Sepsis), ("MON-STABLE", "ICU-02", PatientScenario.Stable)]);
        var now = T0;
        for (int i = 0; i < 48; i++)        // 4 hours of simulated time in 5-minute steps
        {
            now = now.AddMinutes(5);
            await sim.TickAsync(TimeSpan.FromMinutes(5), now, cts.Token);
        }
        // Wait for the feed to drain.
        for (int i = 0; i < 100 && gateway.Readings < 48 * 2 * 6; i++) await Task.Delay(50);
        Assert.Equal(96, feed.MessagesReceived);
        var septic = engine.Snapshots.Single(s => s.DeviceId == "MON-SEPSIS");
        var stable = engine.Snapshots.Single(s => s.DeviceId == "MON-STABLE");
        Assert.True(septic.News2!.Score >= 5, $"septic NEWS2 {septic.News2.Score}");
        Assert.True(stable.News2!.Score < septic.News2.Score);
        lock (alerts)
        {
            Assert.Contains(alerts, a => a.DeviceId == "MON-SEPSIS" && a.Raised && a.Level >= EscalationLevel.Medium);
            Assert.DoesNotContain(alerts, a => a.DeviceId == "MON-STABLE" && a.Level >= EscalationLevel.Medium);
        }
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task InfusionPumpsAndSensorsOverMqttSenML()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        await using var broker = MqttBroker.Create(port, null!);
        await broker.StartAsync(CancellationToken.None);
        await using var feed = MqttSenMLFeed.Connect("127.0.0.1", port);
        var pumps = new List<InfusionPumpStatus>();
        feed.PumpStatus += s => { lock (pumps) pumps.Add(s); };
        var engine = new MonitoringEngine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gateway = new DeviceGateway(engine, feed);
        var run = gateway.RunAsync(cts.Token);
        // Readiness: repeat the oximeter pack until the gateway has seen it, so the subscription is known to be live
        // (a fixed delay is not enough when the machine is busy).
        await using var sensor = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", port).WithClientId("oxi-03"));
        await sensor.ConnectAsync(CancellationToken.None);
        var pack = new IoTCom.Net.Serialization.SenML.SenMLPackBuilder("OXI-03:").At(T0).Add("spo2", 93, "%", null).Add("hr", 104, "/min", null).Build();
        for (int i = 0; i < 100 && !engine.Snapshots.Any(x => x.DeviceId == "OXI-03"); i++)
        {
            await sensor.PublishAsync("medical/OXI-03/telemetry", IoTCom.Net.Serialization.SenML.SenMLCodec.ToJson(pack), IoTCom.Net.PublishOptions.Default, CancellationToken.None);
            await Task.Delay(100);
        }

        await using var pumpLink = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", port).WithClientId("pump-07"));
        var pump = new InfusionPumpSimulator(pumpLink, "PUMP-07", "NaCl 0.9%", 125, 500);
        await pump.PublishAsync(TimeSpan.FromHours(2), T0);                 // 250 mL infused
        await pump.PublishAsync(TimeSpan.FromMinutes(115), T0.AddMinutes(115));   // 489.6 mL → under 10 minutes left

        for (int i = 0; i < 100 && (pumps.Count < 2 || gateway.Readings < 2); i++) await Task.Delay(50);
        lock (pumps)
        {
            Assert.Equal(2, pumps.Count);
            Assert.Equal(250, pumps[0].VolumeInfusedMl, 1);
            Assert.Equal(120, pumps[0].MinutesRemaining);
            Assert.Equal("NEAR_EMPTY", pumps[1].Alarm);
            Assert.Equal("NaCl 0.9%", pumps[1].Drug);
        }
        var oxi = engine.Snapshots.Single(s => s.DeviceId == "OXI-03");
        Assert.Equal(93, oxi.Latest[VitalParameter.SpO2].Value);
        Assert.Equal(104, oxi.Latest[VitalParameter.HeartRate].Value);
        cts.Cancel();
        await run;
    }
}
