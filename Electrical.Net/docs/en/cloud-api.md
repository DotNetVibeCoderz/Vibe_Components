# Cloud API

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/api-cloud.md)

Package: `Electrical.Cloud` (ASP.NET Core shared framework; depends on PowerFlow and ShortCircuit). It exposes studies over HTTP with a
queued job runner and multi-tenant scenario storage. Start from the `eln-webapi` template or `eln serve`.

## Hosting

```csharp
using Electrical.Cloud;
using Electrical.Core.Hosting;
using Electrical.PowerFlow;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddElectrical(b => b.AddPowerFlow().AddCloud(workers: 2));
var app = builder.Build();
app.MapElectricalApi();          // /api/electrical/...
app.Run();
```

`AddCloud(workers)` registers a `StudyRunner` (from `StudyCatalog.Default`) and an `InMemoryScenarioStore`. Replace the store with
`FileScenarioStore(root)` or your own `IScenarioStore` (register it before `AddCloud`, which uses `TryAdd`).

## Endpoints

All routes are under the prefix (default `/api/electrical`). The tenant is the `X-Tenant` header (default `public`).

| Method & route | Description |
|---|---|
| `GET /info` | Name, version, tagline, credit (`credit`, `kredit`), module list |
| `GET /cases` | Built-in case names |
| `GET /cases/{name}` | Network JSON of a built-in case |
| `POST /studies/{kind}` | Run a study synchronously; returns the result JSON |
| `POST /jobs/{kind}` | Queue a study; returns `202` with the job summary |
| `GET /jobs` | Jobs of the tenant |
| `GET /jobs/{id}` | Job summary with `result` when completed |
| `GET /scenarios` | Scenario names of the tenant |
| `GET/PUT/DELETE /scenarios/{name}` | Load, save or delete a network |

Request body for studies, jobs and `PUT /scenarios/{name}` — one of:

```json
{ "case": "case14" }
{ "matpower": "function mpc = case5 ... " }
{ "network": { ...network JSON... } }
{ ...network JSON... }
```

plus an optional `"parameters"` object.

Built-in study kinds:

| Kind | Parameters | Result |
|---|---|---|
| `powerflow` | `method` (`NewtonRaphson`, `FastDecoupled`, `GaussSeidel`, `DC`, `BackwardForwardSweep`), `enforceQLimits` | Power-flow JSON (`converged`, `iterations`, `lossesMw`, `buses[]`, `branches[]`, …) |
| `dcpowerflow` | – | DC power-flow JSON |
| `contingency` | – | N-1 ranking array |
| `shortcircuit` | `bus`, `type` (`ThreePhase`, `SingleLineToGround`, `LineToLine`, `DoubleLineToGround`); without `bus` all buses are scanned | Fault JSON (`ikKa`, `ipKa`, `ibKa`, `ithKa`, `skMva`, `voltages[]`) or scan rows |

```bash
curl -X POST http://localhost:5300/api/electrical/studies/powerflow \
     -H "Content-Type: application/json" -d '{"case":"case14"}'
curl -X POST http://localhost:5300/api/electrical/studies/shortcircuit \
     -H "Content-Type: application/json" -d '{"case":"case14","parameters":{"bus":4,"type":"SingleLineToGround"}}'
curl -X POST http://localhost:5300/api/electrical/jobs/contingency -H "X-Tenant: utility-a" \
     -H "Content-Type: application/json" -d '{"case":"case9"}'
```

(`eln serve` listens on `http://localhost:5300` by default; the `eln-webapi` template uses the usual ASP.NET Core URLs.)

Invalid input returns `400` with the error message; unknown cases, jobs or scenarios return `404`.

## Study runner

```csharp
await using var runner = StudyCatalog.Default(workers: 2);
var job = await runner.SubmitAsync("utility-a", "powerflow", TestCases.Case14());
var done = await runner.WaitAsync(job.Id);
Console.WriteLine(done.Result!["lossesMw"]);           // 13.393…

runner.Register("losses", (net, p) => new System.Text.Json.Nodes.JsonObject { ["mw"] = PowerFlowSolver.Solve(net).LossesMw });
```

`StudyRunner` is a bounded channel with N worker tasks; jobs move through `Queued → Running → Completed | Failed | Cancelled` and keep the
tenant, timestamps, result and error. `Register(kind, handler)` adds study kinds (`StudyHandler(PowerNetwork, JsonObject parameters) →
JsonNode`); `RunNow` executes synchronously.

## JSON network contract

`NetworkJson.ToJson/FromJson/Serialize/Parse` convert a `PowerNetwork` (buses, loads, generators with costs and machine data, branches) to
and from JSON; `ToJson(PowerFlowResult)` and `ToJson(contingencies)` serialise results. Everything uses `JsonNode` (AOT-safe). The tests check
that a JSON round trip of case33bw gives identical losses.

```csharp
string json = NetworkJson.Serialize(TestCases.Case9(), indented: true);
var back = NetworkJson.Parse(json);
IScenarioStore store = new FileScenarioStore("scenarios");
```

## Deployment notes

- The API has no authentication built in; put it behind your gateway or add ASP.NET Core authentication/authorization to the route group
  returned by `MapElectricalApi`.
- `X-Tenant` isolates jobs and scenarios logically; it is not a security boundary on its own.
- Long studies should use `/jobs` (queued) rather than `/studies` (synchronous).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/api-cloud.md)*
