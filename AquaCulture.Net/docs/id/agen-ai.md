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

## Belajar dari data tambak (v1.2)

```csharp
using AquaCulture.AI.Learning;

// riwayat DO per jam dari penyimpanan (atau DoHistory.Simulate untuk demo)
var riwayat = DoHistory.FromSeries(await store.SeriesAsync("P-01", SensorKind.DissolvedOxygen, dari, sampai),
                                   await store.SeriesAsync("P-01", SensorKind.Temperature, dari, sampai),
                                   aerator: await store.SeriesAsync("P-01", SensorKind.AeratorCurrent, dari, sampai));

var gbt = new GbtDoForecaster(horizon: 12).Train(riwayat);           // ML.NET FastTree, satu model per jam ke depan
foreach (var s in gbt.Skill) Console.WriteLine($"+{s.HorizonHours} jam  RMSE {s.Rmse:0.00}  skill {s.SkillScore:P0}");
var kurva = gbt.Forecast(riwayat);                                    // nilai tengah + pita empiris 90 %
if (kurva.FirstRiskBelow(3.0) is { } t) Console.WriteLine($"DO bisa turun di bawah 3 mg/L pukul {t:HH:mm}");

// alternatif LSTM (paket AquaCulture.AI.Torch + backend libtorch seperti TorchSharp-cpu)
using var lstm = new AquaCulture.AI.Torch.LstmDoForecaster(horizon: 12).Train(riwayat);

// pertumbuhan: sesuaikan prior sintetis dengan data sampling tambak sendiri
var baris = FarmGrowthLearner.FromCycles(farm.Cycles, (kolam, hari) => airHarian(kolam, hari));
var prediktor = FarmGrowthLearner.Fit(SpeciesCatalog.Vaname, baris);
```

- `Skill` menyisihkan 20 % riwayat terakhir dan membandingkan setiap horizon dengan dua prakiraan naif: DO tetap seperti sekarang, dan DO
  sama dengan jam yang sama kemarin. Pada riwayat uji 45 hari, model gradient boosting jelas lebih baik dari keduanya pada 3 dan 6 jam. Pada
  12 jam, model hanya sedikit lebih baik dari "jam yang sama kemarin", karena tutupan awan esok hari tidak termasuk dalam inputnya. Tambahkan
  fitur prakiraan cuaca untuk horizon yang lebih panjang.
- `FarmGrowthLearner` mengubah setiap pasangan sampling berurutan menjadi satu baris latih (pertumbuhan % bobot tubuh per hari terhadap suhu,
  DO subuh, NH₃, kepadatan, dan pakan yang benar-benar diberikan dibanding tabel pakan). Dalam tes, tambak yang tumbuh 30 % lebih cepat dari
  katalog berhasil memangkas galat hold-out menjadi separuhnya setelah lima siklus.

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
