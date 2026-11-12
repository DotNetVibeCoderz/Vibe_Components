using System.Net;
using AquaCulture.IoT;
using AquaCulture.IoT.IoTCom;
using IoTCom.Net;
using IoTCom.Net.Protocols.LoRaWan;
using IoTCom.Net.Protocols.Lwm2m;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transports;

namespace AquaCulture.Tests;

public class IoTComGatewayTests
{
    private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(25);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task LoRaWan_network_server_decrypts_gateway_uplinks_into_pond_readings()
    {
        var net = new InMemoryDatagramNetwork(1);
        var serverEp = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1700);
        var node = new LoRaPondNode("70B3D57ED0000001", "P-04", "lora-04", DevAddr: "26011F01",
            NwkSKey: "2B7E151628AED2A6ABF7158809CF4F3C", AppSKey: "3C4FCF098815F7ABA6D2AE2816157E2B");
        await using var ns = new LoRaWanPondNetwork([node], o => o.UseInMemory(net, serverEp));
        var seen = new List<SensorReading>();
        ns.ReadingReceived += r => { lock (seen) seen.Add(r); };
        await ns.ConnectAsync();

        var gw = SemtechPacketForwarder.Create(o =>
        {
            o.GatewayEui = Eui64.Parse("AA555A0000000001");
            o.Server = serverEp;
            o.UseInMemory(net, new IPEndPoint(IPAddress.Parse("10.0.0.2"), 40000));
        });
        await gw.ConnectAsync();
        var device = LoRaWanEndDevice.Abp(Eui64.Parse(node.DevEui), DevAddr.Parse(node.DevAddr!), LoRaWanSessionKeys.FromHex(node.NwkSKey!, node.AppSKey!));
        var phy = device.CreateUplink(2, LoRaPayload.EncodePond(4.6, 29.7, 7.9, 17, 0.4, 3.7), false, false);
        await gw.ForwardAsync(new SemtechRxPacket { Data = phy, Frequency = 921.4, DataRate = "SF9BW125", CodingRate = "4/5", Rssi = -97, Snr = 6.5, CrcStatus = 1, Tmst = 1000 });

        await Until(() => { lock (seen) return seen.Any(r => r.Kind == SensorKind.Rssi); });
        lock (seen)
        {
            Assert.All(seen, r => Assert.Equal("P-04", r.PondId));
            Assert.Equal(4.6, seen.Single(r => r.Kind == SensorKind.DissolvedOxygen).Value, 1);
            Assert.Equal(-97, seen.Single(r => r.Kind == SensorKind.Rssi).Value);
        }
        Assert.Equal(1, ns.Uplinks);
        await ns.SendCommandAsync(new ActuatorCommand("lora-04", "aerator_on"));
        Assert.Equal(1, ns.Sessions.Single().PendingDownlinks);
        await gw.DisconnectAsync();
    }

    [Fact]
    public async Task Lwm2m_gateway_observes_ipso_objects_and_switches_the_aerator()
    {
        var net = new InMemoryDatagramNetwork(2);
        var serverEp = new IPEndPoint(IPAddress.Parse("10.0.1.1"), 5683);
        await using var gwy = new Lwm2mPondGateway(o => o.UseInMemory(net, serverEp));
        var seen = new List<SensorReading>();
        gwy.ReadingReceived += r => { lock (seen) seen.Add(r); };
        await gwy.ConnectAsync();

        await using var client = Lwm2mClient.Create(o =>
        {
            o.EndpointName = "P-07.nbiot-3";
            o.UseServer(serverEp);
            o.UseInMemory(net, new IPEndPoint(IPAddress.Parse("10.0.1.2"), 56830));
        });
        client.AddInstance(Lwm2mPondGateway.Temperature, 0).Set(Lwm2mPondGateway.SensorValue, 29.4);
        client.AddInstance(Lwm2mPondGateway.GenericSensor, 0).Set(Lwm2mPondGateway.ApplicationType, "DO").Set(Lwm2mPondGateway.SensorValue, 5.1);
        client.AddInstance(Lwm2mPondGateway.Actuation, 0).Set(Lwm2mPondGateway.OnOff, false);
        await client.ConnectAsync();

        await Until(() => { lock (seen) return seen.Any(r => r.Kind == SensorKind.DissolvedOxygen) && seen.Any(r => r.Kind == SensorKind.Temperature); });
        lock (seen)
        {
            Assert.All(seen, r => { Assert.Equal("P-07", r.PondId); Assert.Equal("nbiot-3", r.DeviceId); });
            Assert.Equal(5.1, seen.First(r => r.Kind == SensorKind.DissolvedOxygen).Value, 6);
        }

        client.Instance(Lwm2mPondGateway.Temperature, 0)!.Set(Lwm2mPondGateway.SensorValue, 30.2);   // observed → notification
        await Until(() => { lock (seen) return seen.Any(r => r.Kind == SensorKind.Temperature && Math.Abs(r.Value - 30.2) < 1e-9); });

        await gwy.SendCommandAsync(new ActuatorCommand("P-07.nbiot-3", "aerator_on"));
        // a device without a typed 3306 definition stores the written Boolean as one opaque byte
        var on = client.Instance(Lwm2mPondGateway.Actuation, 0)!.Get(Lwm2mPondGateway.OnOff);
        Assert.True(on is true || on is byte[] { Length: 1 } b && b[0] == 1, $"aerator value {on}");
    }

    [Fact]
    public async Task MBus_reader_converts_meter_records_to_kw_and_kwh()
    {
        var listener = new InMemoryTransportListener("mbus");
        var meter = new MBusSimulatedDevice(5, 12345678, "ABB", 1, 0x02, (_, w) => w
            .Integer(0x03, 1_234_567, 4, 0, 0, MBusFunction.Instantaneous, [])      // energy, Wh
            .Integer(0x2B, 3_700, 4, 0, 0, MBusFunction.Instantaneous, [])          // power, W
            .Integer(0x03, 999_000, 4, 1, 0, MBusFunction.Instantaneous, []));      // stored (billing) value: ignored
        var slave = MBusSlaveSimulator.Create(o => o.ListenInMemory(listener));
        slave.Add(meter);
        await slave.StartAsync();
        try
        {
            await using var reader = new MBusEnergyReader(o => o.UseInMemory(listener), [new MBusAeratorMeter(5, "kincir-P01", "P-01"), new MBusAeratorMeter(9, "absent", "P-02")]);
            await reader.ConnectAsync();
            var r = (await reader.ReadAsync()).ToDictionary(x => x.Kind, x => x.Value);
            Assert.Equal(2, r.Count);
            Assert.Equal(1234.567, r[SensorKind.Energy], 6);
            Assert.Equal(3.7, r[SensorKind.Power], 6);
            Assert.Equal(1.5, MBusEnergyReader.KwhPerKg(1000, 7000, 4000), 9);
        }
        finally { await slave.StopAsync(); }
    }
}
