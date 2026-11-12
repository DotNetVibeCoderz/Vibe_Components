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

## Reinforcement learning dengan aksi kontinu (v1.2, Gravicode.RLNet)

```csharp
using AquaCulture.AI.Rl;

// aerasi per jam: aksi = fraksi daya aerator 0–1, reward = −(tarif PLN × kWh + biaya stres DO)
var aerasi = AerationPolicy.Train(steps: 20_000);                    // SAC, sekitar satu menit CPU
var hasil = AerationEnvironment.Evaluate("sac", aerasi.Decide);
var pembanding = AerationEnvironment.Evaluate("set point 5", AerationEnvironment.SetPoint(5));

// pakan harian: aksi = faktor ransum 0,4–1,3 dari tabel pakan; pakan tak termakan menambah biaya dan TAN, serta menurunkan DO subuh
var pakan = FeedingRlPolicy.Train(steps: 15_000);
var f = FeedingEnvironment.Evaluate("sac", pakan.Decide);
var tabel = FeedingEnvironment.Evaluate("tabel + koreksi DO/NH3", FeedingEnvironment.AdjustedTable);
```

Kedua environment adalah kelas RLNet `ContinuousEnvironmentBase` yang digerakkan model platform sendiri: `PondOxygenBudget.StepHour` dengan
cuaca tropis sintetis, dan simulator AquaGrowth dengan neraca TAN kolam. Setiap evaluasi dijalankan pada episode yang tidak dipakai untuk
pelatihan, lalu dibandingkan dengan cara yang benar-benar dipakai petambak.

| Masalah | Kebijakan | Hasil (rata-rata per episode) |
|---|---|---|
| Aerasi, 72 jam | selalu nyala | 482.585 IDR, 0 jam di bawah DO kritis |
| | timer malam 22–06 | 450.243 IDR, 4,8 jam di bawah DO kritis |
| | set point 5 mg/L | 207.005 IDR, 0 jam di bawah DO kritis |
| | **SAC, 20 rb langkah** | 225.034 IDR, 0 jam di bawah DO kritis: kendali mirip set point dipelajari sendiri tanpa diberi set point |
| Pakan, 60 hari | tabel dengan koreksi DO/NH₃ | laba 118,6 jt IDR, 10,9 kg terbuang |
| | **SAC, 15 rb langkah** | **laba 134,7 jt IDR (+14 %)**, 7,5 kg terbuang |

Dengan biaya listrik yang linear, set point yang disetel baik sudah mendekati optimal untuk aerasi. RL lebih bermanfaat bila trade-off-nya
lebih kaya, seperti pada pakan. Latih ulang bila tarif, harga, atau penalti berubah.

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
