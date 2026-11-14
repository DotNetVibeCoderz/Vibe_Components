# API cloud

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/cloud-api.md)

Paket: `Electrical.Cloud` (shared framework ASP.NET Core; bergantung pada PowerFlow dan ShortCircuit). Paket ini menyajikan studi lewat HTTP
dengan runner job berantrean dan penyimpanan skenario multi-tenant. Mulailah dari templat `eln-webapi` atau `eln serve`.

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

`AddCloud(workers)` mendaftarkan `StudyRunner` (dari `StudyCatalog.Default`) dan `InMemoryScenarioStore`. Ganti penyimpanan dengan
`FileScenarioStore(root)` atau `IScenarioStore` Anda sendiri (daftarkan sebelum `AddCloud`, yang memakai `TryAdd`).

## Endpoint

Semua rute berada di bawah prefiks (default `/api/electrical`). Tenant diambil dari header `X-Tenant` (default `public`).

| Metode & rute | Keterangan |
|---|---|
| `GET /info` | Nama, versi, tagline, kredit (`credit`, `kredit`), daftar modul |
| `GET /cases` | Nama kasus bawaan |
| `GET /cases/{name}` | JSON jaringan dari kasus bawaan |
| `POST /studies/{kind}` | Menjalankan studi secara sinkron; mengembalikan JSON hasil |
| `POST /jobs/{kind}` | Memasukkan studi ke antrean; mengembalikan `202` dengan ringkasan job |
| `GET /jobs` | Job milik tenant |
| `GET /jobs/{id}` | Ringkasan job beserta `result` bila selesai |
| `GET /scenarios` | Nama skenario milik tenant |
| `GET/PUT/DELETE /scenarios/{name}` | Memuat, menyimpan, atau menghapus jaringan |

Body permintaan untuk studi, job, dan `PUT /scenarios/{name}` — salah satu dari:

```json
{ "case": "case14" }
{ "matpower": "function mpc = case5 ... " }
{ "network": { ...JSON jaringan... } }
{ ...JSON jaringan... }
```

ditambah objek `"parameters"` opsional.

Jenis studi bawaan:

| Jenis | Parameter | Hasil |
|---|---|---|
| `powerflow` | `method` (`NewtonRaphson`, `FastDecoupled`, `GaussSeidel`, `DC`, `BackwardForwardSweep`), `enforceQLimits` | JSON aliran daya (`converged`, `iterations`, `lossesMw`, `buses[]`, `branches[]`, …) |
| `dcpowerflow` | – | JSON aliran daya DC |
| `contingency` | – | Array peringkat N-1 |
| `shortcircuit` | `bus`, `type` (`ThreePhase`, `SingleLineToGround`, `LineToLine`, `DoubleLineToGround`); tanpa `bus` semua bus dipindai | JSON gangguan (`ikKa`, `ipKa`, `ibKa`, `ithKa`, `skMva`, `voltages[]`) atau baris pemindaian |

```bash
curl -X POST http://localhost:5300/api/electrical/studies/powerflow \
     -H "Content-Type: application/json" -d '{"case":"case14"}'
curl -X POST http://localhost:5300/api/electrical/studies/shortcircuit \
     -H "Content-Type: application/json" -d '{"case":"case14","parameters":{"bus":4,"type":"SingleLineToGround"}}'
curl -X POST http://localhost:5300/api/electrical/jobs/contingency -H "X-Tenant: utility-a" \
     -H "Content-Type: application/json" -d '{"case":"case9"}'
```

(`eln serve` mendengarkan di `http://localhost:5300` secara default; templat `eln-webapi` memakai URL ASP.NET Core biasa.)

Input tidak valid mengembalikan `400` beserta pesan galatnya; kasus, job, atau skenario yang tidak dikenal mengembalikan `404`.

## Runner studi

```csharp
await using var runner = StudyCatalog.Default(workers: 2);
var job = await runner.SubmitAsync("utility-a", "powerflow", TestCases.Case14());
var done = await runner.WaitAsync(job.Id);
Console.WriteLine(done.Result!["lossesMw"]);           // 13.393…

runner.Register("losses", (net, p) => new System.Text.Json.Nodes.JsonObject { ["mw"] = PowerFlowSolver.Solve(net).LossesMw });
```

`StudyRunner` adalah channel berbatas dengan N task pekerja; job berpindah melalui `Queued → Running → Completed | Failed | Cancelled` dan
menyimpan tenant, stempel waktu, hasil, dan galat. `Register(kind, handler)` menambahkan jenis studi (`StudyHandler(PowerNetwork, JsonObject
parameters) → JsonNode`); `RunNow` mengeksekusi secara sinkron.

## Kontrak JSON jaringan

`NetworkJson.ToJson/FromJson/Serialize/Parse` mengonversi `PowerNetwork` (bus, beban, generator beserta biaya dan data mesin, cabang) ke dan dari
JSON; `ToJson(PowerFlowResult)` dan `ToJson(contingencies)` menserialisasi hasil. Semuanya memakai `JsonNode` (aman untuk AOT). Tes memeriksa
bahwa round trip JSON case33bw menghasilkan rugi yang identik.

```csharp
string json = NetworkJson.Serialize(TestCases.Case9(), indented: true);
var back = NetworkJson.Parse(json);
IScenarioStore store = new FileScenarioStore("scenarios");
```

## Catatan deployment

- API tidak memiliki autentikasi bawaan; letakkan di balik gateway Anda atau tambahkan autentikasi/otorisasi ASP.NET Core ke route group yang
  dikembalikan `MapElectricalApi`.
- `X-Tenant` memisahkan job dan skenario secara logis; header ini sendiri bukan batas keamanan.
- Studi yang lama sebaiknya memakai `/jobs` (berantrean), bukan `/studies` (sinkron).

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/cloud-api.md)*
