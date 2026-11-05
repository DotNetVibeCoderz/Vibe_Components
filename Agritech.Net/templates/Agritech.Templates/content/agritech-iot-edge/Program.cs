using Agritech.Core.Farming;
using Agritech.Core.Geo;
using Agritech.Core.Hosting;
using Agritech.Crops;
using Agritech.DigitalTwin;
using Agritech.IoT;

var builder = Host.CreateApplicationBuilder(args);

// Farm layout — replace with your own fields (or load GeoJSON with Agritech.GIS.GeoJson).
var farm = new Farm("My Farm", new GeoCoordinate(-6.3045, 107.3012));
var north = farm.AddField("North paddy", GeoPolygon.Rectangle(new GeoCoordinate(-6.3030, 107.3012), 150, 100), "rice");
north.PlantingDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-25);

builder.Services.AddAgritech(b => b
    .AddCrops()
    .AddIoT(_ => builder.Configuration.GetValue("Simulate", true)
        ? new InMemoryTransport()
        : new MqttSensorTransport(new MqttOptions(Host: builder.Configuration["Mqtt:Host"] ?? "MQTT_BROKER_HOST", FarmId: "farm1")))
    .AddDigitalTwin(farm));
builder.Services.AddHostedService<EdgeWorker>();
builder.Build().Run();

/// <summary>Runs the twin: binds devices, evaluates decisions every minute and (optionally) simulates sensors.</summary>
internal sealed class EdgeWorker(FarmTwin twin, ISensorTransport transport, IConfiguration config, ILogger<EdgeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var field = twin.Farm.Fields[0];
        twin.BindDevice("ws-01", field).BindDevice("soil-01", field);
        twin.Events.Subscribe<Decision>(d => log.LogWarning("DECISION {Title}: {Why}", d.Title, d.Rationale));
        twin.Events.Subscribe<SensorAlert>(a => log.LogInformation("ALERT {Key}: {Message}", a.Key, a.Message));
        await twin.StartAsync(ct);
        log.LogInformation("Edge gateway started for {Farm} ({Fields} field(s))", twin.Farm.Name, twin.Farm.Fields.Count);

        var sim = config.GetValue("Simulate", true)
            ? new SimulatedSensorNetwork(transport, initialSoilMoisture: 28).AddWeatherStation("ws-01").AddSoilProbe("soil-01")
            : null;
        while (!ct.IsCancellationRequested)
        {
            if (sim is not null) await sim.TickAsync(DateTimeOffset.Now, ct: ct);
            twin.Evaluate();
            var f = twin.Snapshot().Fields[0];
            log.LogInformation("{Field}: soil {Soil:0.0}% · air {Air:0.0} °C · RH {Rh:0}%", f.Name, f.SoilMoisture, f.AirTemp, f.Humidity);
            await Task.Delay(TimeSpan.FromSeconds(config.GetValue("IntervalSeconds", 10)), ct);
        }
    }
}
