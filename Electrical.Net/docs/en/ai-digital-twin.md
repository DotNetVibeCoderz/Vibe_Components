# AI and digital twin

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/ai-digital-twin.md)

Packages: `Electrical.DigitalTwin` (depends on PowerFlow, StateEstimation, SCADA, Forecasting) and `Electrical.AI` (depends on PowerFlow,
ShortCircuit, Optimization, Stability, Renewable, Forecasting and DigitalTwin).

## Grid-operator agent (`GridAgent`)

`GridAgent` answers natural-language questions in English or Bahasa Indonesia by letting a chat model call **grounded tools**: every number
in an answer comes from an Electrical.Net solver, never from the model. The language of the question is detected and the answer follows it.

```csharp
var agent = new GridAgent(new GridAgentContext { CaseName = "case14" });   // LLM from ELECTRICAL_LLM_*, else offline rules
AgentAnswer a = await agent.AskAsync("What is the 3-phase short-circuit current at bus 4?");
Console.WriteLine(a.Text);
Console.WriteLine($"[{a.Model}] tools: {string.Join(", ", a.ToolsUsed)} · {a.Language}");

string id = agent.Ask("Mengapa tegangan rendah di case33bw?");   // answered in Bahasa Indonesia
```

`GridAgentContext` sets the default `CaseName` (case9, case14, case33bw, syntheticN), an optional custom `Network` and an optional live
`Twin`. `AskAsync` runs up to `MaxToolRounds` (5) tool-calling rounds and returns `AgentAnswer(Text, ToolsUsed, Model, Language)`.

### Tools (`GridTools.Create`)

| Tool | What it does |
|---|---|
| `grid_overview` | Buses, branches, generators, total load and generation capacity |
| `run_power_flow` | AC Newton–Raphson (optional load scale): convergence, losses, min/max voltage, most loaded branch, violations |
| `contingency_n1` | N-1 branch screening: most severe outages with max loading and minimum voltage |
| `short_circuit` | IEC 60909 maximum fault current at a bus (3ph, slg, ll, dlg) |
| `economic_dispatch` | λ-iteration dispatch, optionally with B-coefficient losses |
| `voltage_stability_margin` | PV-curve loadability margin and L-index |
| `hosting_capacity` | PV hosting capacity at a bus (1.05 pu overvoltage or thermal limit) |
| `forecast_load` | Holt–Winters forecast on 14 days of synthetic history: peak, minimum, energy |
| `explain_alarms` | Limit violations with their causes (live twin if attached, else a power flow) |
| `twin_status` | Live twin: last estimate, bad data, forecast deviation, alarms, asset health (IEEE C57.91 ageing) |

Tool outputs are plain text in the question's language; tool errors are returned as text so the model can recover.

### Chat models

| Type | Use |
|---|---|
| `IChatModel` | `Name` + `CompleteAsync(messages, tools)`; implement it to plug in any backend |
| `OpenAiCompatibleChatModel` | Any OpenAI-compatible Chat Completions endpoint with tool calling: OpenAI, Azure OpenAI (v1 API, `api-key` header), Hugging Face router, Ollama, vLLM, LM Studio. JSON built with `JsonNode` (AOT-safe) |
| `RuleBasedChatModel` | Offline and deterministic: routes the question to tools by bilingual keywords and extracts case, bus, fault type, load scale and hours. No API key needed |

```csharp
var azure = new OpenAiCompatibleChatModel(LlmOptions.AzureOpenAI("https://my-resource.openai.azure.com/", "<key>", "gpt-5-mini"));
var local = new OpenAiCompatibleChatModel(LlmOptions.Ollama("llama3.1"));
var withLlm = new GridAgent(new GridAgentContext { CaseName = "case33bw" }, azure);
var offline = new GridAgent(model: new RuleBasedChatModel());
```

`LlmOptions` factories: `OpenAI(apiKey, model = "gpt-5-mini")`, `AzureOpenAI(endpoint, apiKey, deployment)`,
`HuggingFace(token, model = "Qwen/Qwen2.5-72B-Instruct")`, `Ollama(model = "llama3.1", endpoint = "http://localhost:11434/v1")`, or the
constructor with `LlmProvider.Custom`. `ToString()` never prints the key.

### Configuration from the environment

`LlmOptions.FromEnvironment()` (used by `GridAgent.CreateDefaultModel()`, `AddAI()` and `eln ask`):

| Variable | Meaning |
|---|---|
| `ELECTRICAL_LLM_PROVIDER` | `openai`, `azure`, `hf`, `ollama` or `custom` |
| `ELECTRICAL_LLM_ENDPOINT` | Endpoint URL (required for `azure` and `custom`; optional override otherwise) |
| `ELECTRICAL_LLM_API_KEY` | API key / token (for `openai` falls back to `OPENAI_API_KEY`, for `hf` to `HF_TOKEN`) |
| `ELECTRICAL_LLM_MODEL` | Model or Azure deployment name |

Without `ELECTRICAL_LLM_PROVIDER`, `OPENAI_API_KEY` or `HF_TOKEN` alone are honoured; with nothing configured the agent uses
`RuleBasedChatModel`. Keep keys in environment variables or a secret store, never in source.

```powershell
$env:ELECTRICAL_LLM_PROVIDER = "azure"
$env:ELECTRICAL_LLM_ENDPOINT = "https://my-resource.openai.azure.com/"
$env:ELECTRICAL_LLM_API_KEY  = "<key>"
$env:ELECTRICAL_LLM_MODEL    = "gpt-5-mini"
eln ask "Run a power flow on case14 at 120% load"
```

Dependency injection: `services.AddElectrical(b => b.AddAI())` (optionally `AddAI(llmOptions)`) registers `IChatModel`, a `GridAgentContext`
(case14) and a transient `GridAgent`, plus the modules its tools use.

Offline example (no LLM configured):

```
$ eln ask "What is the 3-phase short-circuit current at bus 4?"
Offline assistant (rule-based) — results from Electrical.Net tools:

Short circuit (ThreePhase) at bus 4 of case14 (138.0 kV): I"k = 5.421 kA, peak ip = 13.170 kA (κ = 1.718), breaking Ib = 5.421 kA, S"k = 1,295.8 MVA, R/X = 0.113.

[model: rule-based (offline) · tools: short_circuit · language: en]
```

```
$ eln ask "Mengapa tegangan rendah di case33bw?"
Asisten offline (berbasis aturan) — hasil perhitungan Electrical.Net:

21 alarm (aliran daya case33bw pada beban ×1.00):
- [High] Tegangan bus 18 0.913 pu di bawah 0.95 karena saluran 8-9 berbeban tinggi (80%)
- [High] Tegangan bus 17 0.914 pu di bawah 0.95 karena saluran 8-9 berbeban tinggi (80%)
...
[model: rule-based (offline) · tools: explain_alarms · language: id]
```

**Validation.** The tool-calling loop is tested against a fake OpenAI-compatible endpoint (request shape, `Bearer` header, 10 tool
definitions, `tool` role messages); the rule-based model is tested on bilingual questions; every tool runs without errors on the test cases. A
live test (opt-in with `ELECTRICAL_LIVE_LLM=1`) passed on Azure OpenAI `gpt-5-mini`, answering a case14 question with the MATPOWER losses
(13.39 MW).

## Analytics

### Anomaly detection

```csharp
var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
double[] load = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, 5, TimeSpan.FromHours(1), 50, noise: 0.01).Values;
var spikes = RobustZScore.DetectSeasonal(load, period: 24);           // MAD-based robust z-score per hour of day
var drift = new EwmaDetector(lambda: 0.2, l: 3).Detect(load);
var shift = new CusumDetector(k: 0.5, h: 5).Detect(load);
```

- `RobustZScore`: median/MAD z-scores (threshold 3.5), optional rolling window, seasonal variant.
- `EwmaDetector` (λ, L): small persistent drifts; `CusumDetector` (k, h): level shifts with direction. Both learn a baseline during `warmup`
  or take one with `WithBaseline`, and also work sample by sample with `Update`.
- Each detection is an `Anomaly(Index, Value, Score, Direction, Method)`.

### Fault classification and location

```csharp
var currents = new ThreePhase(Complex.FromPolarCoordinates(2.0, -1.2), 0, 0);   // kA, phase A only
FaultClassification c = FaultClassifier.Classify(currents);                      // SingleLineToGround, "A", grounded
var voltages = new ThreePhase(new Complex(3, 1), Phasor.FromPolarDeg(7.2, -120), Phasor.FromPolarDeg(7.2, 120));
FaultLocation loc = FaultLocator.Locate(voltages, currents, c, z1OhmPerKm: new Complex(0.1, 0.4), z0OhmPerKm: new Complex(0.3, 1.2));
Console.WriteLine($"{c.Code}: {loc.DistanceKm:0.0} km ({loc.Loop})");
```

`FaultClassifier` uses symmetrical components (Blackburn, *Protective Relaying*): balanced → 3φ, significant I₀ → earth fault (one or two
high phases → SLG / DLG), significant I₂ without I₀ → LL; it reports the faulted phases, `Code` ("AG", "BC", "BCG", "ABC") and bilingual
descriptions. `FaultLocator` applies the reactance method on the faulted loop (residual compensation for earth loops). The tests recover a
12.5 km SLG fault and a 30 km B–C fault with 1 Ω arc resistance exactly, and classify every IEC 60909 fault type from `ShortCircuitStudy`.

### Load-profile clustering

```csharp
var profiles = Enumerable.Range(0, 30)
    .Select(i => SyntheticProfiles.Load((LoadProfileKind)(i % 3), start, 1, TimeSpan.FromHours(1), 1 + i * 0.37, noise: 0.03, seed: 100 + i).Values)
    .ToList();
var clusters = LoadProfileClustering.KMeans(profiles, k: 3);
int cls = clusters.Predict(profiles[0]);
```

k-means with k-means++ seeding (Arthur & Vassilvitskii 2007) and restarts keeping the lowest inertia; profiles are normalised by their peak
so shapes rather than sizes are grouped. Deterministic for a seed. The tests separate residential,
commercial and industrial customers perfectly.

### Reinforcement learning for Volt/VAR control

`VoltVarEnvironment` is a gym-style environment on the Baran–Wu feeder (by default three capacitor/inverter devices, 24-hour episodes with a
residential load shape). Observation = bus voltages + device levels + hour; actions = no-op or ±1 step on one device; reward = −(losses +
voltage-violation penalty + switching cost).

```csharp
var env = new VoltVarEnvironment();
var idle = VoltVarPolicies.Evaluate(env, VoltVarPolicies.DoNothing);
var rule = VoltVarPolicies.Evaluate(env, VoltVarPolicies.VoltageRule);
var q = new TabularQLearning(env.StateCount, env.ActionCount, seed: 7);
var training = q.Train(env, episodes: 400);
var learned = VoltVarPolicies.Evaluate(env, q.Policy);
```

| Policy (one 24 h episode) | Reward | Energy losses | Bus-hours outside 0.95–1.05 pu |
|---|---|---|---|
| Do nothing | −56.40 | 1873 kWh | 129 |
| Voltage rule | −25.20 | 1648 kWh | 60 |
| Tabular Q-learning (400 episodes, seed 7) | −14.51 | 1412 kWh | 10 |

Training is deterministic for a seed. `VoltVarEnvironmentOptions` changes the network, devices, episode length, profile, load scale, noise
and reward weights; `Reset`/`Step` let you plug in your own agent (for example Gravicode.RLNet).

## Digital twin (`GridTwin`)

`GridTwin` keeps a live estimate of a network. Each `Update` cycle:

1. takes the measurements ingested since the last cycle (`Ingest(...)`, or `IngestTags(tagDatabase)` which maps SCADA tags such as
   `BUS4.VM`, `BR3.PF`, `GEN0.P` to measurements and applies breaker statuses `BRk.CLOSED` to the topology);
2. runs WLS state estimation with largest-normalized-residual bad-data removal;
3. derives branch flows, system load, load scale and KPIs from the estimated voltages;
4. compares the load with a forecast (`SetForecast` or `ForecastLoad`);
5. integrates asset thermal ageing;
6. explains limit violations;
7. publishes `TwinStateUpdated`, `TwinAlarmRaised`, `TwinAlarmCleared` and `TwinBadMeasurement` on the `IGridEventBus` (and the `Updated`
   event).

```csharp
var bus = new InMemoryGridEventBus();
using var sub = bus.Subscribe<TwinAlarmRaised>(e => Console.WriteLine(e.Alarm.Localized("id")));
var twin = new GridTwin(TestCases.Case9(), new GridTwinOptions { VoltageLowPu = 0.95, OverloadPercent = 100 }, bus);
var pf = PowerFlowSolver.Solve(TestCases.Case9());
twin.Ingest(MeasurementGenerator.FromPowerFlow(pf));        // or twin.IngestTags(tagDatabase) from SCADA (BUSn.VM, BRk.PF, …)
TwinState s = twin.Update(DateTimeOffset.UtcNow);
Console.WriteLine(twin.Summary("en"));

var w = twin.WhatIf(new OpenBranch(6), new ScaleLoad(1.1));
Console.WriteLine($"{w.Description}: Δlosses {w.DeltaLossesMw:+0.00;-0.00} MW, ΔVmin {w.DeltaMinVoltagePu:+0.000;-0.000} pu");
```

- `TwinState`: step, timestamp, convergence, observability, J, `Vm`/`VaDeg`, bus and branch results, estimated load and scale, forecast
  deviation, removed measurements, explained alarms, `GridKpis`.
- `GridTwinOptions`: bad-data threshold, voltage band, overload and high-loading thresholds, forecast-deviation limit, history capacity,
  auto-rating of unrated branches (125 % of the base-case flow), ambient temperature, transformer thermal parameters, measurement sigmas.
- `History`, `Current`, `AssetHealth`, `OpenBranches()`, `SetBranchStatus`, `OperatingSnapshot()`, `EstimatedLoadSeries()`, `Summary(language)`.
- Thread-safe; DI: `services.AddElectrical(b => b.AddDigitalTwin())` registers `IGridTwinFactory` (and StateEstimation, SCADA, Forecasting).

### What-if simulation

`WhatIfSimulator.Run(baseNetwork, actions)` (or `twin.WhatIf(...)` on the current operating snapshot) solves the base and a modified clone
and compares KPIs (`DeltaLossesMw`, `DeltaMinVoltagePu`, `DeltaMaxLoadingPercent`) with bilingual descriptions and explained alarms.
Actions: `OpenBranch`, `CloseBranch`, `ScaleLoad(factor, busId?)`, `AddPv(bus, MW, Mvar)`, `AddShuntCapacitor(bus, Mvar)`,
`SetGeneration(index, MW)`.

```csharp
var ws = WhatIfSimulator.Run(TestCases.Case33BW(), [new AddShuntCapacitor(30, 1.0), new AddPv(18, 0.5)]);
```

### Explained alarms

`AlarmExplainer.Explain(powerFlowResult)` turns violations into `ExplainedAlarm(Kind, Element, Value, Limit, Severity, Message, MessageId,
Causes)`: undervoltage attributed to the most loaded upstream line, overvoltage at light load, overloads attributed to open branches, plus
forecast deviation, bad data and estimator failure. Example (case33bw): "Bus 18 voltage 0.913 pu below 0.95 because line 8-9 is heavily
loaded (80%)".

### Asset health (IEEE C57.91)

`AssetHealthTracker` integrates loading-based thermal ageing: transformers with the IEEE C57.91 steady-state hot spot
θ<sub>H</sub> = θ<sub>A</sub> + Δθ<sub>TO,R</sub>·((K²R + 1)/(R + 1))ⁿ + Δθ<sub>H,R</sub>·K<sup>2m</sup> and the aging acceleration factor
F<sub>AA</sub> = exp(15000/383 − 15000/(θ<sub>H</sub> + 273)) (1 at 110 °C, normal life 180 000 h); lines with a simplified conductor
temperature (θ<sub>A</sub> + 45 K·K²) referenced to 75 °C. `AssetHealth` reports loading, hot spot, F<sub>AA</sub>, F<sub>EQA</sub>,
equivalent aging hours, health index and status (Good / Fair / Poor, Baik / Cukup / Buruk). Example: at 120 % load and 30 °C ambient the
default transformer reaches 133.8 °C hot spot with F<sub>AA</sub> = 9.89.

### Closed-loop simulator (`TwinSimulator`)

`TwinSimulator` drives a twin deterministically: a "real" grid follows a daily load profile with seeded noise and scripted events
(`TwinScenarioEvent`), is solved by Newton–Raphson, published through an `RtuSimulator` as Modbus registers, scanned into a `TagDatabase`,
optionally corrupted (`TwinBadDataInjection`), ingested by the twin and estimated.

```csharp
var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions { Steps = 24 });
TwinSimulationResult run = sim.Run();
Console.WriteLine($"mean |ΔV| {run.MeanVoltageErrorPu:0.00000} pu, max {run.MaxVoltageErrorPu:0.00000} pu");
foreach (var alarm in run.States[^1].Alarms) Console.WriteLine(alarm);
foreach (var asset in run.AssetHealth.OrderByDescending(h => h.AgingFactor).Take(3)) Console.WriteLine(asset);
```

```
$ eln twin --steps 24
Digital twin of case14: 24 cycles, state-estimation voltage error mean 0.00017 pu, max 0.00077 pu
...
Explained alarms:
  [Warning] Bus 7 voltage 1.076 pu above 1.05 because the system is lightly loaded (45% of nominal) and line charging/capacitors dominate
  ...
Asset health (worst):
  line 9-10: 73.4% load, 54.3 °C, F_AA 0.065, F_EQA 0.068, health 100 (Good)
```

**Validation.** Over a 24-hour case14 run the estimate stays within 0.005 pu of the true voltages (mean < 0.0015 pu) and within 3 % of the true
load; a 150 MW gross error on `BR3.PF` is removed and published; a 20 % low forecast raises one forecast-deviation alarm; IEEE C57.91 gives
110 °C at rated load and F<sub>AA</sub> = 1 at 110 °C, ≈ 2 at 116.8 °C; what-if capacitors/PV improve the 33-bus feeder and opening the
substation feeder is reported as non-converged (islanded).

## Limitations

- The agent's answers are only as good as its tools; it does not design networks or change set-points. Use the LLM for explanation, keep
  operating decisions with operators.
- The rule-based model understands a limited bilingual vocabulary and extracts simple arguments only.
- The twin uses the balanced bus-branch model and static WLS (no dynamic estimation); asset ageing is steady-state per cycle.
- Tabular Q-learning suits the small default action space; larger problems need function approximation (for example Gravicode.RLNet or
  TorchSharp).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/ai-digital-twin.md)*
