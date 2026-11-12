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
