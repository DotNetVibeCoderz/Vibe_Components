# AI and the AquaAgent

## Predictive models (`AquaCulture.AI`)

| Type | Model | Trained on |
|---|---|---|
| `GrowthPredictor` | Gradient-boosted trees (own implementation), permutation importance | Synthetic weekly growth from the AquaGrowth engine over random environments (deterministic); call `Train(dataset)` with farm records to calibrate |
| `MortalityPredictor` | L2 logistic regression, AUC reported | Synthetic stress scenarios of the mortality model |
| `DiseasePredictor` | Noisy-OR of knowledge-base risks + recent mortality | — |
| `HarvestOutlook` | Monte-Carlo TGC (Rust kernel) | — |
| `FeedOptimizationPolicy` | Tabular Q-learning over DO × temperature × NH₃ | Rewards simulated with the growth engine (value gained − feed − losses) |
| `MlNetGrowthModel` | ML.NET SDCA regression; save/load `.zip` | Any `GrowthRecord` list |
| `OnnxTabularModel` | ONNX Runtime | Models exported from scikit-learn, XGBoost, LightGBM, PyTorch, ML.NET |

Building blocks in `AquaCulture.AI.Ml`: `Dataset`, `StandardScaler`, `RidgeRegression`, `LogisticRegression`, `RegressionTree`, `GradientBoostedTrees`, `Metrics`.

## Learning from farm data (v1.2)

```csharp
using AquaCulture.AI.Learning;

// hourly DO history from the store (or DoHistory.Simulate for a demo)
var history = DoHistory.FromSeries(await store.SeriesAsync("P-01", SensorKind.DissolvedOxygen, from, to),
                                   await store.SeriesAsync("P-01", SensorKind.Temperature, from, to),
                                   aerator: await store.SeriesAsync("P-01", SensorKind.AeratorCurrent, from, to));

var gbt = new GbtDoForecaster(horizon: 12).Train(history);           // ML.NET FastTree, one model per hour ahead
foreach (var s in gbt.Skill) Console.WriteLine($"+{s.HorizonHours} h  RMSE {s.Rmse:0.00}  skill {s.SkillScore:P0}");
var curve = gbt.Forecast(history);                                    // point + empirical 90 % band
if (curve.FirstRiskBelow(3.0) is { } t) Console.WriteLine($"DO may fall below 3 mg/L at {t:HH:mm}");

// LSTM alternative (package AquaCulture.AI.Torch + a libtorch backend such as TorchSharp-cpu)
using var lstm = new AquaCulture.AI.Torch.LstmDoForecaster(horizon: 12).Train(history);

// growth: adapt the synthetic prior to this farm's own samplings
var rows = FarmGrowthLearner.FromCycles(farm.Cycles, (pond, day) => dailyWater(pond, day));
var predictor = FarmGrowthLearner.Fit(SpeciesCatalog.Vaname, rows);
```

- `Skill` holds out the last 20 % of the history and compares each horizon with two naive forecasts: persistence and the same hour
  yesterday. On the 45-day test history the gradient-boosted model clearly beats both at 3 and 6 hours. At 12 hours it is only slightly
  better than the same hour yesterday, because the next day's cloud cover is not in its inputs. Add weather-forecast features for
  longer horizons.
- `FarmGrowthLearner` turns every pair of consecutive samplings into a training row (growth in % body weight per day against temperature,
  dawn DO, NH₃, density and the feed actually given compared with the feeding table). In the tests, a farm that grows 30 % faster than the
  catalogue halves the hold-out error after five cycles.

## Reinforcement learning with continuous actions (v1.2, Gravicode.RLNet)

```csharp
using AquaCulture.AI.Rl;

// hourly aeration: action = aerator power fraction 0–1, reward = −(PLN tariff × kWh + cost of DO stress)
var aeration = AerationPolicy.Train(steps: 20_000);                 // SAC, about a minute of CPU
var result = AerationEnvironment.Evaluate("sac", aeration.Decide);
var baseline = AerationEnvironment.Evaluate("set-point 5", AerationEnvironment.SetPoint(5));

// daily feeding: action = ration factor 0.4–1.3 of the feeding table; uneaten feed costs money, adds TAN and lowers dawn DO
var feeding = FeedingRlPolicy.Train(steps: 15_000);
var f = FeedingEnvironment.Evaluate("sac", feeding.Decide);
var table = FeedingEnvironment.Evaluate("table + DO/NH3 corrections", FeedingEnvironment.AdjustedTable);
```

Both environments are RLNet `ContinuousEnvironmentBase` classes driven by the platform's own models: `PondOxygenBudget.StepHour` with
synthetic tropical weather, and the AquaGrowth simulator with a pond TAN balance. Every evaluation runs on held-out episodes, compared with
what farmers actually do.

| Problem | Policy | Result (mean per episode) |
|---|---|---|
| Aeration, 72 h | always on | 482,585 IDR, 0 h below critical DO |
| | night timer 22–06 | 450,243 IDR, 4.8 h below critical |
| | set point 5 mg/L | 207,005 IDR, 0 h below critical |
| | **SAC, 20k steps** | 225,034 IDR, 0 h below critical: it learned set-point-like control without being given a set point |
| Feeding, 60 days | table with DO/NH₃ corrections | 118.6 M IDR profit, 10.9 kg wasted |
| | **SAC, 15k steps** | **134.7 M IDR profit (+14 %)**, 7.5 kg wasted |

With a linear electricity cost, a well-tuned set point is already close to optimal for aeration. RL pays off when the trade-off is
richer, as in feeding. Re-train when tariffs, prices or penalties change.

## AquaAgent

```csharp
var agent = new AquaAgent(new AquaContext
{
    Species = SpeciesCatalog.Vaname, PondName = "K-01", AreaM2 = 2000, DepthM = 1.3, AbwG = 11.8, Population = 223_600,
    Water = new WaterSample { TemperatureC = 30.2, DissolvedOxygen = 4.2, Ph = 8.3, Ammonia = 1.1, SalinityPpt = 18 },
});
var answer = await agent.AskAsync("Berapa pakan udang hari ini dan apakah oksigen aman malam ini?");
Console.WriteLine(answer.Text);         // answers in the question's language
Console.WriteLine(string.Join(", ", answer.ToolsUsed));
```

Tools: `pond_overview`, `water_quality_check`, `feeding_plan`, `growth_projection`, `harvest_forecast`, `disease_diagnosis`, `disease_risk`,
`do_forecast`, `weather_impact`, `economics`. The system prompt tells the model to take every number from a tool.

### Several ponds, memory and voice (v1.2)

```csharp
var memory = new AgentMemory("farm-memory.json");            // notes persist between sessions (BM25, Indonesian + English)
var agent = new AquaAgent([pond1, pond2, pond3], memory: memory);
await agent.AskAsync("Bagaimana kondisi semua kolam?");       // list_ponds: worst water first
await agent.AskAsync("Berapa pakan untuk kolam 3 hari ini?");  // "kolam 3" selects P-03 automatically
await agent.AskAsync("Tolong ingat bahwa udang di kolam 3 mulai lemas pagi ini");   // remember (tagged with the pond)

// voice: OpenAI-compatible /audio/transcriptions and /audio/speech (OpenAI, Azure OpenAI v1, or a local gateway)
var voice = new OpenAiVoiceService(LlmOptions.FromEnvironment()!);
VoiceAnswer v = await agent.AskVoiceAsync(voice, File.ReadAllBytes("question.wav"));   // transcript, grounded answer, MP3
```

- Relevant notes (BM25 over the question) are added to the system prompt, and the model can also call `recall`.
- History is cut in whole turns (`MaxHistoryMessages`), so a tool result never loses its call.
- Transcription asks for Indonesian by default and gives the model a prompt of farm vocabulary (kolam, kincir, vaname…).

### Providers

| `AQUA_LLM_PROVIDER` | Settings |
|---|---|
| `azure` | `AQUA_LLM_ENDPOINT` (resource URL), `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` (deployment, e.g. `gpt-5-mini`) — uses the v1 API |
| `openai` | `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` |
| `anthropic` | `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` (e.g. `claude-sonnet-5-5`), `AQUA_LLM_WORKSPACE` for keys not scoped to a workspace |
| `deepseek` | `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` |
| `hf` | `AQUA_LLM_API_KEY` or `HF_TOKEN` (Hugging Face router) |
| `ollama` | `AQUA_LLM_MODEL`, optional `AQUA_LLM_ENDPOINT` |
| `custom` | any OpenAI-compatible endpoint |

With nothing configured, `RuleBasedChatModel` routes questions to tools by English and Indonesian keywords, so the agent also works offline on an edge device.
Both Azure OpenAI (gpt-5-mini) and Anthropic (claude-sonnet-5-5) were tested live with these tools.
