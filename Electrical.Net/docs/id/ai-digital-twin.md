# AI dan digital twin

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/ai-digital-twin.md)

Paket: `Electrical.DigitalTwin` (bergantung pada PowerFlow, StateEstimation, SCADA, Forecasting) dan `Electrical.AI` (bergantung pada PowerFlow,
ShortCircuit, Optimization, Stability, Renewable, Forecasting, dan DigitalTwin).

## Agen operator jaringan (`GridAgent`)

`GridAgent` menjawab pertanyaan bahasa alami dalam bahasa Inggris atau Bahasa Indonesia dengan membiarkan model chat memanggil **tool yang
membumi**: setiap angka dalam jawaban berasal dari solver Electrical.Net, bukan dari model. Bahasa pertanyaan dideteksi dan jawabannya
mengikuti bahasa tersebut.

```csharp
var agent = new GridAgent(new GridAgentContext { CaseName = "case14" });   // LLM dari ELECTRICAL_LLM_*, atau aturan offline
AgentAnswer a = await agent.AskAsync("What is the 3-phase short-circuit current at bus 4?");
Console.WriteLine(a.Text);
Console.WriteLine($"[{a.Model}] tools: {string.Join(", ", a.ToolsUsed)} · {a.Language}");

string id = agent.Ask("Mengapa tegangan rendah di case33bw?");   // dijawab dalam Bahasa Indonesia
```

`GridAgentContext` menentukan `CaseName` default (case9, case14, case33bw, syntheticN), `Network` kustom opsional, dan `Twin` langsung opsional.
`AskAsync` menjalankan hingga `MaxToolRounds` (5) putaran tool calling dan mengembalikan `AgentAnswer(Text, ToolsUsed, Model, Language)`.

### Tool (`GridTools.Create`)

| Tool | Fungsi |
|---|---|
| `grid_overview` | Bus, cabang, generator, total beban, dan kapasitas pembangkitan |
| `run_power_flow` | Newton–Raphson AC (skala beban opsional): konvergensi, rugi, tegangan min/maks, cabang terbebani, pelanggaran |
| `contingency_n1` | Penyaringan cabang N-1: gangguan terparah dengan pembebanan maksimum dan tegangan minimum |
| `short_circuit` | Arus gangguan maksimum IEC 60909 di sebuah bus (3ph, slg, ll, dlg) |
| `economic_dispatch` | Dispatch iterasi λ, opsional dengan rugi koefisien-B |
| `voltage_stability_margin` | Margin loadability kurva PV dan indeks-L |
| `hosting_capacity` | Hosting capacity PV di sebuah bus (batas tegangan lebih 1,05 pu atau termal) |
| `forecast_load` | Prakiraan Holt–Winters dari 14 hari riwayat sintetis: puncak, minimum, energi |
| `explain_alarms` | Pelanggaran batas beserta penyebabnya (twin langsung bila terpasang, selain itu aliran daya) |
| `twin_status` | Twin langsung: estimasi terakhir, bad data, deviasi prakiraan, alarm, kesehatan aset (penuaan IEEE C57.91) |

Keluaran tool berupa teks biasa dalam bahasa pertanyaan; galat tool dikembalikan sebagai teks agar model dapat memulihkan diri.

### Model chat

| Tipe | Kegunaan |
|---|---|
| `IChatModel` | `Name` + `CompleteAsync(messages, tools)`; implementasikan untuk memasang backend apa pun |
| `OpenAiCompatibleChatModel` | Endpoint Chat Completions kompatibel OpenAI dengan tool calling: OpenAI, Azure OpenAI (API v1, header `api-key`), router Hugging Face, Ollama, vLLM, LM Studio. JSON dibangun dengan `JsonNode` (aman AOT) |
| `RuleBasedChatModel` | Offline dan deterministik: mengarahkan pertanyaan ke tool berdasarkan kata kunci dwibahasa dan mengekstrak kasus, bus, jenis gangguan, skala beban, dan jam. Tidak butuh API key |

```csharp
var azure = new OpenAiCompatibleChatModel(LlmOptions.AzureOpenAI("https://my-resource.openai.azure.com/", "<key>", "gpt-5-mini"));
var local = new OpenAiCompatibleChatModel(LlmOptions.Ollama("llama3.1"));
var withLlm = new GridAgent(new GridAgentContext { CaseName = "case33bw" }, azure);
var offline = new GridAgent(model: new RuleBasedChatModel());
```

Factory `LlmOptions`: `OpenAI(apiKey, model = "gpt-5-mini")`, `AzureOpenAI(endpoint, apiKey, deployment)`,
`HuggingFace(token, model = "Qwen/Qwen2.5-72B-Instruct")`, `Ollama(model = "llama3.1", endpoint = "http://localhost:11434/v1")`, atau konstruktor
dengan `LlmProvider.Custom`. `ToString()` tidak pernah mencetak key.

### Konfigurasi dari environment

`LlmOptions.FromEnvironment()` (dipakai oleh `GridAgent.CreateDefaultModel()`, `AddAI()`, dan `eln ask`):

| Variabel | Arti |
|---|---|
| `ELECTRICAL_LLM_PROVIDER` | `openai`, `azure`, `hf`, `ollama`, atau `custom` |
| `ELECTRICAL_LLM_ENDPOINT` | URL endpoint (wajib untuk `azure` dan `custom`; opsional untuk lainnya) |
| `ELECTRICAL_LLM_API_KEY` | API key / token (untuk `openai` fallback ke `OPENAI_API_KEY`, untuk `hf` ke `HF_TOKEN`) |
| `ELECTRICAL_LLM_MODEL` | Nama model atau deployment Azure |

Tanpa `ELECTRICAL_LLM_PROVIDER`, `OPENAI_API_KEY` atau `HF_TOKEN` saja tetap dikenali; bila tidak ada yang diset, agen memakai
`RuleBasedChatModel`. Simpan key di environment variable atau secret store, jangan di kode sumber.

```powershell
$env:ELECTRICAL_LLM_PROVIDER = "azure"
$env:ELECTRICAL_LLM_ENDPOINT = "https://my-resource.openai.azure.com/"
$env:ELECTRICAL_LLM_API_KEY  = "<key>"
$env:ELECTRICAL_LLM_MODEL    = "gpt-5-mini"
eln ask "Jalankan aliran daya case14 pada beban 120%"
```

Dependency injection: `services.AddElectrical(b => b.AddAI())` (atau `AddAI(llmOptions)`) mendaftarkan `IChatModel`, `GridAgentContext` (case14),
dan `GridAgent` transient, ditambah modul yang dipakai tool-nya.

Contoh offline (tanpa LLM):

```
$ eln ask "Mengapa tegangan rendah di case33bw?"
Asisten offline (berbasis aturan) — hasil perhitungan Electrical.Net:

21 alarm (aliran daya case33bw pada beban ×1.00):
- [High] Tegangan bus 18 0.913 pu di bawah 0.95 karena saluran 8-9 berbeban tinggi (80%)
- [High] Tegangan bus 17 0.914 pu di bawah 0.95 karena saluran 8-9 berbeban tinggi (80%)
...
[model: rule-based (offline) · tools: explain_alarms · language: id]
```

```
$ eln ask "What is the 3-phase short-circuit current at bus 4?"
Offline assistant (rule-based) — results from Electrical.Net tools:

Short circuit (ThreePhase) at bus 4 of case14 (138.0 kV): I"k = 5.421 kA, peak ip = 13.170 kA (κ = 1.718), breaking Ib = 5.421 kA, S"k = 1,295.8 MVA, R/X = 0.113.

[model: rule-based (offline) · tools: short_circuit · language: en]
```

**Validasi.** Loop tool calling diuji terhadap endpoint tiruan kompatibel OpenAI (bentuk request, header `Bearer`, 10 definisi tool, pesan
ber-role `tool`); model berbasis aturan diuji dengan pertanyaan dwibahasa; setiap tool berjalan tanpa galat pada kasus uji. Tes live (diaktifkan
dengan `ELECTRICAL_LIVE_LLM=1`) lulus di Azure OpenAI `gpt-5-mini`, menjawab pertanyaan case14 dengan rugi MATPOWER (13,39 MW).

## Analitik

### Deteksi anomali

```csharp
var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
double[] load = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, 5, TimeSpan.FromHours(1), 50, noise: 0.01).Values;
var spikes = RobustZScore.DetectSeasonal(load, period: 24);           // robust z-score berbasis MAD per jam dalam sehari
var drift = new EwmaDetector(lambda: 0.2, l: 3).Detect(load);
var shift = new CusumDetector(k: 0.5, h: 5).Detect(load);
```

- `RobustZScore`: z-score median/MAD (ambang 3,5), jendela bergulir opsional, varian musiman.
- `EwmaDetector` (λ, L): drift kecil yang persisten; `CusumDetector` (k, h): pergeseran level beserta arahnya. Keduanya mempelajari baseline
  selama `warmup` atau menerimanya lewat `WithBaseline`, dan juga bekerja per sampel dengan `Update`.
- Setiap deteksi berupa `Anomaly(Index, Value, Score, Direction, Method)`.

### Klasifikasi dan lokasi gangguan

```csharp
var currents = new ThreePhase(Complex.FromPolarCoordinates(2.0, -1.2), 0, 0);   // kA, hanya fasa A
FaultClassification c = FaultClassifier.Classify(currents);                      // SingleLineToGround, "A", ke tanah
var voltages = new ThreePhase(new Complex(3, 1), Phasor.FromPolarDeg(7.2, -120), Phasor.FromPolarDeg(7.2, 120));
FaultLocation loc = FaultLocator.Locate(voltages, currents, c, z1OhmPerKm: new Complex(0.1, 0.4), z0OhmPerKm: new Complex(0.3, 1.2));
Console.WriteLine($"{c.Code}: {loc.DistanceKm:0.0} km ({loc.Loop})");
```

`FaultClassifier` memakai komponen simetris (Blackburn, *Protective Relaying*): seimbang → 3φ, I₀ signifikan → gangguan tanah (satu atau dua fasa
tinggi → SLG / DLG), I₂ signifikan tanpa I₀ → LL; hasilnya berisi fasa terganggu, `Code` ("AG", "BC", "BCG", "ABC"), dan deskripsi dwibahasa.
`FaultLocator` menerapkan metode reaktansi pada loop yang terganggu (kompensasi residual untuk loop tanah). Tes memperoleh kembali gangguan SLG
12,5 km dan gangguan B–C 30 km dengan resistansi busur 1 Ω secara eksak, serta mengklasifikasikan setiap jenis gangguan IEC 60909 dari
`ShortCircuitStudy`.

### Klasterisasi profil beban

```csharp
var profiles = Enumerable.Range(0, 30)
    .Select(i => SyntheticProfiles.Load((LoadProfileKind)(i % 3), start, 1, TimeSpan.FromHours(1), 1 + i * 0.37, noise: 0.03, seed: 100 + i).Values)
    .ToList();
var clusters = LoadProfileClustering.KMeans(profiles, k: 3);
int cls = clusters.Predict(profiles[0]);
```

k-means dengan inisialisasi k-means++ (Arthur & Vassilvitskii 2007) dan beberapa restart yang menyimpan inersia terendah; profil dinormalisasi
terhadap puncaknya sehingga yang dikelompokkan adalah bentuk, bukan ukuran. Deterministik untuk sebuah seed. Tes memisahkan pelanggan
residensial, komersial, dan industri dengan sempurna.

### Reinforcement learning untuk kendali Volt/VAR

`VoltVarEnvironment` adalah lingkungan bergaya gym pada penyulang Baran–Wu (default tiga perangkat kapasitor/inverter, episode 24 jam dengan
bentuk beban residensial). Observasi = tegangan bus + level perangkat + jam; aksi = no-op atau ±1 langkah pada satu perangkat; reward = −(rugi +
penalti pelanggaran tegangan + biaya switching).

```csharp
var env = new VoltVarEnvironment();
var idle = VoltVarPolicies.Evaluate(env, VoltVarPolicies.DoNothing);
var rule = VoltVarPolicies.Evaluate(env, VoltVarPolicies.VoltageRule);
var q = new TabularQLearning(env.StateCount, env.ActionCount, seed: 7);
var training = q.Train(env, episodes: 400);
var learned = VoltVarPolicies.Evaluate(env, q.Policy);
```

| Kebijakan (satu episode 24 jam) | Reward | Rugi energi | Bus-jam di luar 0,95–1,05 pu |
|---|---|---|---|
| Tidak berbuat apa-apa | −56,40 | 1873 kWh | 129 |
| Aturan tegangan | −25,20 | 1648 kWh | 60 |
| Q-learning tabular (400 episode, seed 7) | −14,51 | 1412 kWh | 10 |

Pelatihan deterministik untuk sebuah seed. `VoltVarEnvironmentOptions` mengubah jaringan, perangkat, panjang episode, profil, skala beban,
derau, dan bobot reward; `Reset`/`Step` memungkinkan Anda memasang agen sendiri (misalnya Gravicode.RLNet).

## Digital twin (`GridTwin`)

`GridTwin` menjaga estimasi langsung sebuah jaringan. Setiap siklus `Update`:

1. mengambil pengukuran yang masuk sejak siklus sebelumnya (`Ingest(...)`, atau `IngestTags(tagDatabase)` yang memetakan tag SCADA seperti
   `BUS4.VM`, `BR3.PF`, `GEN0.P` ke pengukuran dan menerapkan status pemutus `BRk.CLOSED` ke topologi);
2. menjalankan estimasi keadaan WLS dengan penghapusan bad data largest normalized residual;
3. menurunkan aliran cabang, beban sistem, skala beban, dan KPI dari tegangan hasil estimasi;
4. membandingkan beban dengan prakiraan (`SetForecast` atau `ForecastLoad`);
5. mengintegrasikan penuaan termal aset;
6. menjelaskan pelanggaran batas;
7. memublikasikan `TwinStateUpdated`, `TwinAlarmRaised`, `TwinAlarmCleared`, dan `TwinBadMeasurement` di `IGridEventBus` (dan event `Updated`).

```csharp
var bus = new InMemoryGridEventBus();
using var sub = bus.Subscribe<TwinAlarmRaised>(e => Console.WriteLine(e.Alarm.Localized("id")));
var twin = new GridTwin(TestCases.Case9(), new GridTwinOptions { VoltageLowPu = 0.95, OverloadPercent = 100 }, bus);
var pf = PowerFlowSolver.Solve(TestCases.Case9());
twin.Ingest(MeasurementGenerator.FromPowerFlow(pf));        // atau twin.IngestTags(tagDatabase) dari SCADA (BUSn.VM, BRk.PF, …)
TwinState s = twin.Update(DateTimeOffset.UtcNow);
Console.WriteLine(twin.Summary("id"));

var w = twin.WhatIf(new OpenBranch(6), new ScaleLoad(1.1));
Console.WriteLine($"{w.DescriptionId}: Δrugi {w.DeltaLossesMw:+0.00;-0.00} MW, ΔVmin {w.DeltaMinVoltagePu:+0.000;-0.000} pu");
```

- `TwinState`: langkah, stempel waktu, konvergensi, observabilitas, J, `Vm`/`VaDeg`, hasil bus dan cabang, estimasi beban dan skalanya, deviasi
  prakiraan, pengukuran yang dihapus, alarm yang dijelaskan, `GridKpis`.
- `GridTwinOptions`: ambang bad data, pita tegangan, ambang beban lebih dan pembebanan tinggi, batas deviasi prakiraan, kapasitas riwayat,
  rating otomatis untuk cabang tanpa rating (125 % aliran kasus dasar), suhu ambien, parameter termal trafo, sigma pengukuran.
- `History`, `Current`, `AssetHealth`, `OpenBranches()`, `SetBranchStatus`, `OperatingSnapshot()`, `EstimatedLoadSeries()`, `Summary(language)`.
- Thread-safe; DI: `services.AddElectrical(b => b.AddDigitalTwin())` mendaftarkan `IGridTwinFactory` (beserta StateEstimation, SCADA,
  Forecasting).

### Simulasi what-if

`WhatIfSimulator.Run(baseNetwork, actions)` (atau `twin.WhatIf(...)` pada snapshot operasi saat ini) menyelesaikan kasus dasar dan klon yang
dimodifikasi, lalu membandingkan KPI (`DeltaLossesMw`, `DeltaMinVoltagePu`, `DeltaMaxLoadingPercent`) dengan deskripsi dwibahasa
(`Description`, `DescriptionId`) dan alarm yang dijelaskan. Aksi: `OpenBranch`, `CloseBranch`, `ScaleLoad(factor, busId?)`, `AddPv(bus, MW, Mvar)`,
`AddShuntCapacitor(bus, Mvar)`, `SetGeneration(index, MW)`.

```csharp
var ws = WhatIfSimulator.Run(TestCases.Case33BW(), [new AddShuntCapacitor(30, 1.0), new AddPv(18, 0.5)]);
```

### Alarm yang dijelaskan

`AlarmExplainer.Explain(powerFlowResult)` mengubah pelanggaran menjadi `ExplainedAlarm(Kind, Element, Value, Limit, Severity, Message, MessageId,
Causes)`: tegangan rendah dikaitkan dengan saluran hulu paling terbebani, tegangan lebih saat beban ringan, beban lebih dikaitkan dengan cabang
yang terbuka, ditambah deviasi prakiraan, bad data, dan kegagalan estimator. Contoh (case33bw): "Tegangan bus 18 0.913 pu di bawah 0.95 karena
saluran 8-9 berbeban tinggi (80%)".

### Kesehatan aset (IEEE C57.91)

`AssetHealthTracker` mengintegrasikan penuaan termal berbasis pembebanan: trafo dengan hot spot tunak IEEE C57.91
θ<sub>H</sub> = θ<sub>A</sub> + Δθ<sub>TO,R</sub>·((K²R + 1)/(R + 1))ⁿ + Δθ<sub>H,R</sub>·K<sup>2m</sup> dan faktor percepatan penuaan
F<sub>AA</sub> = exp(15000/383 − 15000/(θ<sub>H</sub> + 273)) (1 pada 110 °C, umur normal 180 000 jam); saluran dengan suhu penghantar
sederhana (θ<sub>A</sub> + 45 K·K²) yang dirujuk ke 75 °C. `AssetHealth` melaporkan pembebanan, hot spot, F<sub>AA</sub>, F<sub>EQA</sub>, jam
penuaan ekivalen, indeks kesehatan, dan status (Good / Fair / Poor, Baik / Cukup / Buruk). Contoh: pada beban 120 % dan ambien 30 °C trafo default
mencapai hot spot 133,8 °C dengan F<sub>AA</sub> = 9,89.

### Simulator loop tertutup (`TwinSimulator`)

`TwinSimulator` menggerakkan twin secara deterministik: jaringan "nyata" mengikuti profil beban harian dengan derau ber-seed dan kejadian
terjadwal (`TwinScenarioEvent`), diselesaikan dengan Newton–Raphson, dipublikasikan lewat `RtuSimulator` sebagai register Modbus, dipindai ke
`TagDatabase`, opsional dirusak (`TwinBadDataInjection`), lalu dimasukkan ke twin dan diestimasi.

```csharp
var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions { Steps = 24 });
TwinSimulationResult run = sim.Run();
Console.WriteLine($"rata-rata |ΔV| {run.MeanVoltageErrorPu:0.00000} pu, maks {run.MaxVoltageErrorPu:0.00000} pu");
foreach (var alarm in run.States[^1].Alarms) Console.WriteLine(alarm.Localized("id"));
foreach (var asset in run.AssetHealth.OrderByDescending(h => h.AgingFactor).Take(3)) Console.WriteLine(asset);
```

```
$ eln twin --steps 24 --lang id
Digital twin of case14: 24 cycles, state-estimation voltage error mean 0.00017 pu, max 0.00077 pu
...
Alarm yang dijelaskan:
  [Warning] Tegangan bus 1 1.060 pu di atas 1.05 karena beban sistem ringan (45% dari nominal) sehingga kapasitansi saluran/kapasitor dominan
  ...
```

**Validasi.** Dalam simulasi case14 selama 24 jam, estimasi tetap dalam 0,005 pu dari tegangan sebenarnya (rata-rata < 0,0015 pu) dan dalam 3 %
dari beban sebenarnya; galat besar 150 MW pada `BR3.PF` dihapus dan dipublikasikan; prakiraan yang 20 % terlalu rendah memunculkan satu alarm
deviasi prakiraan; IEEE C57.91 memberi 110 °C pada beban pengenal dan F<sub>AA</sub> = 1 pada 110 °C, ≈ 2 pada 116,8 °C; what-if kapasitor/PV
memperbaiki penyulang 33-bus, dan membuka penyulang gardu induk dilaporkan tidak konvergen (terpisah menjadi pulau).

## Batasan

- Jawaban agen hanya sebaik tool-nya; agen tidak merancang jaringan atau mengubah set-point. Gunakan LLM untuk penjelasan, keputusan operasi
  tetap di tangan operator.
- Model berbasis aturan memahami kosakata dwibahasa yang terbatas dan hanya mengekstrak argumen sederhana.
- Twin memakai model bus-cabang seimbang dan WLS statis (tanpa estimasi dinamis); penuaan aset bersifat tunak per siklus.
- Q-learning tabular cocok untuk ruang aksi default yang kecil; masalah yang lebih besar memerlukan aproksimasi fungsi (misalnya
  Gravicode.RLNet atau TorchSharp).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/ai-digital-twin.md)*
