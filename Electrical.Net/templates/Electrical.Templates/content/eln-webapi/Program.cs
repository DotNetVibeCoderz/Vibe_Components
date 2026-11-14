// Power-system REST API — built with Electrical.Net (Gravicode Studios, led by Kang Fadhil)
// Try:  curl -X POST http://localhost:5000/api/electrical/studies/powerflow -H "Content-Type: application/json" -d '{"case":"case14"}'
using Electrical.Cloud;
using Electrical.Core.Hosting;
using Electrical.PowerFlow;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddElectrical(b => b.AddPowerFlow().AddCloud(workers: 2));

var app = builder.Build();
app.MapGet("/", () => "Electrical.Net API — see /api/electrical/info");
app.MapElectricalApi();
app.Run();
