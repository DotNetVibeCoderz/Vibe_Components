# Edge gateway, offline sync and performance (v2.1)

*Made by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](../id/edge-platform.md)

## NativeAOT edge gateway (`samples/AquaCulture.EdgeGateway`, `aqua-edge`)

A pond gateway has to keep the aerators right when the internet is down. `aqua-edge` decides everything locally and only
*syncs* with the cloud:

```
probes (simulator / Modbus TCP) → EdgeAnalytics (outliers, stuck probes, calibration) → EdgeOutbox (disk)
                                                     ├─ AlertEngine (species thresholds, hysteresis)
                                                     └─ aerators: DO set point ± hysteresis, switched on early when the
                                                        dawn-DO forecast falls below the species' critical DO
EdgeOutbox ⇄ CloudSyncClient ⇄ aqua-cloud (/t/{tenant}/sync/{gateway}/…)   readings up, settings down
```

```bash
# Raspberry Pi 4/5 with a 64-bit OS (publish on an arm64 Linux host or runner; NativeAOT does not cross-compile from Windows)
sudo apt-get install -y clang zlib1g-dev
dotnet publish samples/AquaCulture.EdgeGateway -c Release -r linux-arm64 -o out
scp -r out/aqua-edge out/runtimes pi@gateway:/opt/aqua-edge/

/opt/aqua-edge/aqua-edge --selftest --require-native      # 48 simulated hours in about a second; exit code 0 = healthy
AQUA_SYNC_KEY=<tenant key> /opt/aqua-edge/aqua-edge --modbus 192.168.1.50:502 --pond P-01 --device probe-1 --aerator aer-1 \
    --cloud https://cloud.example.com --tenant koperasi-a --gateway gw-tambak-1 --data /var/lib/aqua-edge
```

- One self-contained executable of about 4–5 MB (no .NET runtime on the Pi), plus the Rust kernels in `runtimes/<rid>/native`.
- The project treats every trim/AOT warning as an error, so a dependency that is not AOT-safe fails the build instead of failing on the Pi.
- CI publishes it for `linux-arm64` (on an arm64 runner) and `linux-x64` and runs `--selftest --require-native` on the native binary.
- On Windows, publishing needs the Visual Studio C++ tools (`vswhere.exe` on `PATH`).

The self-test drives the whole pipeline: 3 ponds, readings every 5 minutes, a restart halfway, and a cloud that drops every 5th
request and every 7th response. It checks that the Rust kernels are loaded and match the managed formulas, that alerts and
aerator commands happened, that the cloud setting was applied, and that every reading reached the cloud exactly once.

A systemd unit for the Pi:

```ini
[Unit]
Description=AquaCulture.Net edge gateway
After=network.target
[Service]
ExecStart=/opt/aqua-edge/aqua-edge --modbus 192.168.1.50:502 --pond P-01 --device probe-1 --aerator aer-1 --cloud https://cloud.example.com --tenant koperasi-a --data /var/lib/aqua-edge
Environment=AQUA_SYNC_KEY=<tenant key>
Restart=always
[Install]
WantedBy=multi-user.target
```

## Offline-first sync (`AquaCulture.IoT`, `AquaCulture.Data`, `aqua-cloud`)

```csharp
using var outbox = new EdgeOutbox("/var/lib/aqua-edge/outbox");     // JSON-lines segments + an "acked" watermark
outbox.Append(readings);                                             // every reading gets the next sequence number

var sync = new CloudSyncClient(http, new Uri("https://cloud.example.com/"), "koperasi-a", apiKey, "gw-tambak-1");
sync.ConfigChanged += cfg => setPoint = cfg.Number("doSetPoint") ?? setPoint;
await sync.RunAsync(outbox, TimeSpan.FromMinutes(1), ct: stop);      // backoff 5 s → 5 min while offline
```

| Concern | How it is handled |
|---|---|
| Link down for days | Readings wait in the outbox on disk; the gateway keeps controlling the aerators locally |
| Power cut mid-write | A torn last line is discarded when the outbox is reopened; numbering continues after the last complete reading |
| Retry after a lost response | The cloud keeps a watermark per gateway (`sync_gateways` table); readings at or below it are skipped, so nothing is stored twice |
| SD card filling up | `MaxBytes` (default 256 MB) drops the oldest segments and counts them in `Dropped` |
| Settings from the office | `PUT /t/{tenant}/sync/{gateway}/config` (any JSON object); gateways poll with `If-None-Match` and keep the last copy on disk |

Cloud endpoints, all with the tenant's `X-Api-Key`:

| Endpoint | Purpose |
|---|---|
| `POST /t/{tenant}/sync/{gateway}/readings` | Batch upload (`SyncWire` format); answers `{"acked": n, "stored": …, "duplicates": …}` |
| `GET` / `PUT /t/{tenant}/sync/{gateway}/config` | Gateway settings with an ETag |
| `GET /t/{tenant}/sync/gateways` | Watermark, totals and last contact of every gateway |

`AquaStore.IngestGatewayBatchAsync` stores the batch and advances the watermark in one transaction. Databases created by 2.0
get the `sync_gateways` table automatically on SQLite. For other providers, add it with a migration.

## Benchmarks and performance budgets (`benchmarks/AquaCulture.Benchmarks`)

```bash
dotnet run -c Release --project benchmarks/AquaCulture.Benchmarks -- --filter "*"                                   # BenchmarkDotNet
dotnet run -c Release --project benchmarks/AquaCulture.Benchmarks -- --budgets budgets.json --job short --filter "*"  # what CI runs
```

- `KernelBenchmarks` runs every Rust kernel against its managed fallback: advection–diffusion on 200 × 200 cells, DO saturation and
  NH₃ for 10 000 samples, statistics of 100 000 values, and 1 000 Monte-Carlo growth runs.
- `ModelBenchmarks` covers the per-reading and per-hour work of a farm server or gateway: the 48-hour oxygen budget, a 120-day growth
  run, a water-quality assessment, the edge filter, the learned 12-hour DO forecast, and an EPCIS document.

`budgets.json` holds a ceiling for each benchmark: mean time per operation in µs, and bytes allocated per operation. The run
exits with code 1 when a budget is exceeded or a budgeted benchmark did not run, and writes a table to the GitHub job summary.

- Time budgets are about 4× a desktop run, because hosted runners are slower and noisy.
- Allocation budgets are tight, because allocations are deterministic. The kernels must allocate nothing.
- Release publishing waits for the budgets.

Baseline (Windows 11, .NET 10, x64):

| Benchmark | Rust | Managed |
|---|---:|---:|
| Advection–diffusion step, 200 × 200 | 526 µs | 926 µs |
| DO saturation × 10 000 | 107 µs | 133 µs |
| TGC Monte-Carlo, 1 000 × 120 days | 1.6 ms | 1.9 ms |

| Model | Mean | Allocated |
|---|---:|---:|
| Oxygen budget, 48 h | 10 µs | 5.4 KB |
| Growth, 120 days | 28 µs | 15 KB |
| Water-quality assessment | 0.8 µs | 1 KB |
| Edge filter, per reading | 0.8 µs | 750 B |
| GBT DO forecast, 12 h | 39 µs | 2.4 KB |
