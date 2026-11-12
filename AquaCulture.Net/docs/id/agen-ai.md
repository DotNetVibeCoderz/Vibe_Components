# AI dan AquaAgent

## Model prediktif

| Tipe | Model | Data latih |
|---|---|---|
| `GrowthPredictor` | Gradient-boosted trees, kepentingan fitur permutasi | Pertumbuhan mingguan sintetis dari mesin AquaGrowth; kalibrasi dengan `Train(dataset)` data tambak Anda |
| `MortalityPredictor` | Regresi logistik L2 (AUC dilaporkan) | Skenario stres sintetis |
| `DiseasePredictor` | Noisy-OR risiko basis pengetahuan + kematian terbaru | — |
| `HarvestOutlook` | Monte-Carlo TGC (kernel Rust) | — |
| `FeedOptimizationPolicy` | Q-learning tabular atas DO × suhu × NH₃ | Imbalan disimulasikan dengan mesin pertumbuhan |
| `MlNetGrowthModel` | Regresi SDCA ML.NET (simpan/muat `.zip`) | `GrowthRecord` |
| `OnnxTabularModel` | ONNX Runtime | Model dari scikit-learn, XGBoost, LightGBM, PyTorch, ML.NET |

## AquaAgent

```csharp
var agen = new AquaAgent(new AquaContext { Species = SpeciesCatalog.Vaname, AbwG = 11.8, Population = 223_600 });
var jawaban = await agen.AskAsync("Berapa pakan udang hari ini dan apakah oksigen aman malam ini?");
Console.WriteLine(jawaban.Text);   // menjawab sesuai bahasa pertanyaan, angka diambil dari alat
```

Alat: `pond_overview`, `water_quality_check`, `feeding_plan`, `growth_projection`, `harvest_forecast`, `disease_diagnosis`, `disease_risk`,
`do_forecast`, `weather_impact`, `economics`.

### Penyedia LLM

| `AQUA_LLM_PROVIDER` | Pengaturan |
|---|---|
| `azure` | `AQUA_LLM_ENDPOINT`, `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` (nama deployment, mis. `gpt-5-mini`) |
| `openai` | `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` |
| `anthropic` | `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` (mis. `claude-sonnet-5-5`), `AQUA_LLM_WORKSPACE` bila kunci tidak terikat workspace |
| `deepseek` | `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL` |
| `hf` | `AQUA_LLM_API_KEY` atau `HF_TOKEN` |
| `ollama` | `AQUA_LLM_MODEL`, opsional `AQUA_LLM_ENDPOINT` |

Tanpa pengaturan apa pun, `RuleBasedChatModel` memilih alat berdasarkan kata kunci Indonesia/Inggris sehingga agen tetap berjalan offline.
Azure OpenAI (gpt-5-mini) dan Anthropic (claude-sonnet-5-5) sudah diuji langsung.
