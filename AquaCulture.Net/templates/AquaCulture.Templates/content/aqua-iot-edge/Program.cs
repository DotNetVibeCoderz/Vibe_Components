// AquaCulture.Net edge gateway (Raspberry Pi / industrial PC): MQTT sensors → farm digital twin → aerator commands.
// Made by Gravicode Studios, led by Kang Fadhil.
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.DigitalTwin;
using AquaCulture.IoT;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Gateway>();
builder.Build().Run();

internal sealed class Gateway(IConfiguration config, ILogger<Gateway> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var farm = new Farm { Id = config["Farm:Id"] ?? "farm1", Name = config["Farm:Name"] ?? "My farm" };
        var pond = new Pond { Id = "K-01", AreaM2 = 2000, DepthM = 1.3, AerationKw = 4.4, System = CultureSystem.LinedPond };
        farm.Ponds.Add(pond);
        farm.Cycles.Add(new CultureCycle { Id = "C1", PondId = pond.Id, Species = SpeciesCatalog.Get("SPECIES_ID"),
            StockingDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-45), StockedCount = 200_000, StockingWeightG = 0.002 }.LogSampling(DateOnly.FromDateTime(DateTime.Today), 6));

        ISensorTransport transport = config["Mqtt:Host"] is { Length: > 0 } host
            ? new MqttSensorTransport(new MqttOptions(host, int.Parse(config["Mqtt:Port"] ?? "1883"), FarmId: farm.Id, Username: config["Mqtt:Username"], Password: config["Mqtt:Password"]))
            : new SimulatedSensorTransport([new SimulatedPond(pond.Id, "sim-1", SalinityPpt: 18)]);

        await using var twin = new FarmTwin(farm, transport) { AutoActuate = bool.Parse(config["AutoActuate"] ?? "true") };
        twin[pond.Id].AeratorDeviceId = config["Devices:Aerator"] ?? "aerator-k01";
        await twin.StartAsync(ct);
        log.LogInformation("Gateway started ({Transport})", transport.GetType().Name);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(int.Parse(config["EvaluateSeconds"] ?? "60")));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (transport is SimulatedSensorTransport sim) sim.Emit(DateTime.Now);
            foreach (var d in await twin.EvaluateAsync(DateTime.Now, ct))
                log.LogInformation("[{Severity}] {Pond} {Kind}: {Title}", d.Severity, d.PondId, d.Kind, d.Title.En);
        }
    }
}
