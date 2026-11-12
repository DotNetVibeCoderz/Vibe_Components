using System.Net;
using System.Net.Sockets;
using AquaCulture.Core.Geo;
using AquaCulture.IoT;
using AquaCulture.IoT.IoTCom;
using IoTCom.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Protocols.Sparkplug;
using IoTCom.Net.Transports;

namespace AquaCulture.Tests;

public class IoTComTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(25);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task Modbus_transport_polls_probe_and_switches_aerator_coil()
    {
        var store = new ModbusDataStore(16, 16, 16, 16);
        store.HoldingRegisters.Write(0, [512, 291, 785, unchecked((ushort)(short)-120), 205, 343]);
        var listener = new InMemoryTransportListener("plc");
        await using var server = ModbusServer.Create(o => o.ListenInMemory(listener).WithStore(store).WithUnitIds(7));
        await server.StartAsync();

        IoTComProbe[] probes = [new(7, "probe-7", "P-01", Modbus.TypicalProbeMap, AeratorCoil: 3)];
        await using var t = new IoTComModbusTransport(o => o.UseInMemory(listener).WithTimeout(TimeSpan.FromSeconds(2)), probes);
        var seen = new List<SensorReading>();
        t.ReadingReceived += seen.Add;
        await t.ConnectAsync(startPolling: false);
        Assert.True(t.IsConnected);

        var r = (await t.PollOnceAsync()).ToDictionary(x => x.Kind, x => x.Value);
        Assert.Equal(5.12, r[SensorKind.DissolvedOxygen], 6);
        Assert.Equal(29.1, r[SensorKind.Temperature], 6);
        Assert.Equal(-120, r[SensorKind.Orp]);
        Assert.Equal(6, seen.Count);

        await t.SendCommandAsync(new ActuatorCommand("probe-7", "aerator_on"));
        Assert.True(store.Coils[3]);
        await t.SendCommandAsync(new ActuatorCommand("probe-7", "aerator_off"));
        Assert.False(store.Coils[3]);
        await Assert.ThrowsAsync<NotSupportedException>(() => t.SendCommandAsync(new ActuatorCommand("probe-7", "feed")));
    }

    [Fact]
    public void SenML_round_trips_json_and_cbor_and_reads_foreign_packs()
    {
        var t0 = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        SensorReading[] rows =
        [
            new("probe-1", "P-01", SensorKind.Temperature, 29.4, t0, "°C"),
            new("probe-1", "P-01", SensorKind.DissolvedOxygen, 4.85, t0, "mg/L"),
            new("probe-2", "P-02", SensorKind.Humidity, 81, t0.AddMinutes(5), "%"),
        ];
        var json = AquaSenML.Encode(rows);
        Assert.Contains("\"u\":\"Cel\"", System.Text.Encoding.UTF8.GetString(json));
        Assert.Equal(rows, AquaSenML.Decode(json));
        Assert.Equal(rows, AquaSenML.Decode(AquaSenML.Encode(rows, cbor: true), cbor: true));

        // a pack from another vendor: base name, base time, relative time, unknown parameter
        var foreign = """[{"bn":"urn:dev:esp32-07:","bt":1794538800,"n":"temp","u":"Cel","v":28.9},{"n":"do","v":5.6,"t":60},{"n":"rssi_raw","v":3}]"""u8.ToArray();
        var f = AquaSenML.Decode(foreign, defaultPond: "P-09");
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.Equal("P-09", x.PondId));
        Assert.Equal("esp32-07", f[0].DeviceId);
        Assert.Equal(SensorKind.DissolvedOxygen, f[1].Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), f[1].Timestamp - f[0].Timestamp);
    }

    [Fact]
    public void Nmea_decoder_reads_water_temperature_depth_and_mooring_drift()
    {
        var anchor = new GeoCoordinate(-5.85, 106.60);
        var d = new NmeaCageDecoder("kja-gnss", "KJA-01", anchor, driftAlarmM: 30);
        var now = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

        var mtw = d.Feed(NmeaSentence.Build("YX", "MTW", ["29.8", "C"]), now);
        Assert.Equal(29.8, Assert.Single(mtw).Value);
        Assert.Equal(SensorKind.Temperature, mtw[0].Kind);
        Assert.Equal(1250, Assert.Single(d.Feed(NmeaSentence.Build("SD", "DPT", ["12.3", "0.2"]), now)).Value, 6);
        Assert.Equal(1180, Assert.Single(d.Feed(NmeaSentence.Build("SD", "DBT", ["38.7", "f", "11.8", "M", "6.4", "F"]), now)).Value, 6);
        Assert.Empty(d.Feed("$YXMTW,29.8,C*00", now));                  // bad checksum
        Assert.Equal(1, d.Rejected);

        var (lat, ns) = NmeaParser.FormatCoordinate(anchor.Latitude + 0.0001, true);
        var (lon, ew) = NmeaParser.FormatCoordinate(anchor.Longitude, false);
        d.Feed(NmeaSentence.Build("GP", "GGA", ["030000.00", lat, ns, lon, ew, "1", "08", "0.9", "1.0", "M", "0.0", "M", "", ""]), now);
        Assert.InRange(d.DriftM!.Value, 5, 20);
        Assert.False(d.IsDrifting);

        (lat, ns) = NmeaParser.FormatCoordinate(anchor.Latitude + 0.0010, true);
        d.Feed(NmeaSentence.Build("GP", "GGA", ["030100.00", lat, ns, lon, ew, "1", "08", "0.9", "1.0", "M", "0.0", "M", "", ""]), now);
        Assert.True(d.IsDrifting);
    }

    private static string Ais(uint mmsi, double lat, double lon, double sog, string name = "")
    {
        var report = new AisPositionReport(1, 0, mmsi) { Latitude = lat, Longitude = lon, SpeedOverGround = sog, CourseOverGround = 90, Name = name };
        return AisBits.ToSentences(AisBits.EncodePosition(report), 'A', 0, "AI")[0];
    }

    [Fact]
    public void Cage_zone_watch_flags_fast_vessels_near_or_inside_the_zone()
    {
        var center = new GeoCoordinate(-5.85, 106.60);
        var watch = new CageZoneWatch(GeoPolygon.Rectangle(center, 200, 200), bufferM: 300, minSpeedKnots: 2);

        Assert.Null(watch.Feed(Ais(525000001, -5.85, 106.62, 12)));                    // ≈ 2 km away
        var near = watch.Feed(Ais(525000002, -5.85, GeoMath.Destination(center, 250, 90).Longitude, 8));
        Assert.NotNull(near);
        Assert.False(near!.Inside);
        Assert.InRange(near.DistanceM, 100, 200);
        Assert.Contains("wake", near.Message.En);
        Assert.Null(watch.Feed(Ais(525000003, -5.85, GeoMath.Destination(center, 250, 90).Longitude, 0.5)));   // slow: no wake
        var inside = watch.Feed(Ais(525000004, -5.8501, 106.6001, 0.2));
        Assert.True(inside!.Inside);
        Assert.Contains("zona KJA", inside.Message.Id);
        Assert.Equal(4, watch.Vessels.Count);
    }

    [Fact]
    public async Task Sparkplug_publisher_reaches_a_scada_host_and_receives_aerator_writes()
    {
        int port = FreePort();
        var broker = MqttBroker.Create(port, null!);
        await broker.StartAsync();
        try
        {
            void Mqtt(MqttEndpointOptions o) => o.UseBroker("127.0.0.1", port);
            await using var host = SparkplugHost.Create(o => { o.HostId = "scada"; o.Mqtt = Mqtt; });
            await host.StartAsync();
            await using var pub = new SparkplugPondPublisher("Indramayu", "farm-01", ["P-01", "P-02"], Mqtt);
            var commands = new List<ActuatorCommand>();
            pub.CommandReceived += c => { lock (commands) commands.Add(c); };
            await pub.StartAsync();

            var now = DateTime.UtcNow;
            await pub.PublishAsync([
                new SensorReading("probe-1", "P-01", SensorKind.DissolvedOxygen, 4.2, now, "mg/L"),
                new SensorReading("probe-1", "P-01", SensorKind.Temperature, 30.1, now, "°C"),
                new SensorReading("probe-2", "P-03", SensorKind.Ph, 8.0, now, ""),     // unknown pond: ignored
            ]);
            await Until(() => host.Find("Indramayu", "farm-01", "P-01") is { } v && v.Metrics.TryGetValue("DissolvedOxygen", out var m) && m.AsDouble() == 4.2);
            Assert.Equal(30.1, host.Find("Indramayu", "farm-01", "P-01")!.Metrics["Temperature"].AsDouble());
            Assert.True(host.Find("Indramayu", "farm-01", "P-02")!.Online);

            await host.WriteAsync("Indramayu", "farm-01", "P-02", "Aerator", true);
            await Until(() => { lock (commands) return commands.Count > 0; });
            Assert.Equal(new ActuatorCommand("P-02", "aerator_on", 1, commands[0].Timestamp), commands[0]);
        }
        finally { await broker.StopAsync(); }
    }
}
