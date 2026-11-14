# CLI (`eln`)

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/cli.md)

```bash
dotnet tool install -g Electrical.Cli
eln help
```

From source: `dotnet run --project tools/Electrical.Cli -- <command> [options]`.

**Network input** (most commands): `--case case9 | case14 | case33bw | synthetic<N>` or `--file grid.m | grid.json | grid.xml` (MATPOWER,
Electrical.Net JSON, CIM RDF/XML). The default case is `case14` unless a command states otherwise.

## Commands

| Command | Options | Description |
|---|---|---|
| `info` | | Framework, modules and native kernel status |
| `cases` | | Built-in test networks |
| `pf` | `--method nr\|fd\|gs\|dc\|bfs`, `--scale 1.2`, `--qlim`, `--json`, `--out sld.svg` | Power flow |
| `n1` | `--top 15` | N-1 branch contingency ranking |
| `plf` | `--samples 500`, `--std 0.1`, `--seed 2026` | Probabilistic (Monte-Carlo) load flow |
| `bench` | `--sizes 100,1000,3000` | Power-flow scaling benchmark on synthetic grids |
| `sld` | `--out sld.svg`, `--literal`, `--dark` | Single-line diagram with power-flow overlay |
| `geojson` | `--out grid.geojson`, `--svg map.svg`, `--lat -6.2 --lon 106.8` | GIS export |
| `convert` | `--out grid.m\|grid.json\|grid.xml` | MATPOWER / JSON / CIM conversion |
| `rtu` | `--port 1502`, `--seconds 0` | Modbus TCP RTU simulator serving power-flow measurements |
| `serve` | `--urls http://localhost:5300` | REST API (Electrical.Cloud) |
| `sc` | `--bus 4 --type 3ph\|slg\|ll\|llg` | IEC 60909 short circuit (all-bus scan without `--bus`) |
| `stability` | `--bus 8`, `--clear 0.083`, `--trip 8-9` | Transient stability (classical model) and critical clearing time |
| `pv` | `--step 0.05` | PV curve (loadability margin) and L-index |
| `modes` | | Small-signal eigenvalues (frequency, damping) |
| `harmonic` | `--bus 9`, `--source 6pulse\|vfd\|led`, `--pu 0.1` | Harmonic load flow, THD and IEEE 519 check |
| `arcflash` | `--kv 0.48 --ika 30 --time 0.2`, `--dist 455`, `--equipment switchgear` | IEEE 1584 arc flash |
| `relay` | `--pickup 400 --tms 0.1`, `--curve iec-si`, `--current 4000` | Relay operating time |
| `ed` | `--losses` | Economic dispatch (B-coefficient losses with `--losses`) |
| `opf` | `--limit 5-6:60` | DC-OPF with LMPs (optional line limit override in MW) |
| `uc` | | 24 h unit commitment (Wood & Wollenberg 3-unit example) |
| `se` | `--bad 2-3` | WLS state estimation with bad-data detection (gross error injected on a flow) |
| `forecast` | `--method hw\|reg\|knn\|ens`, `--days 14` | Day-ahead load forecast on a synthetic profile |
| `reconfig` | | Feeder reconfiguration, 33-bus with 5 tie switches |
| `vvo` | | Volt/VAR optimization, 33-bus |
| `caps` | | Optimal capacitor placement, 33-bus |
| `hosting` | `--bus 18`, `--max 5` | PV hosting capacity at a bus (33-bus) |
| `qsts` | `--days 1`, `--pv 1.5` | Quasi-static time series (15-min load + PV) |
| `ask` | `"<question>"`, `--case case14` | AI grid agent (LLM via `ELECTRICAL_LLM_*`, offline otherwise) |
| `twin` | `--steps 24`, `--lang en\|id` | Digital twin: SCADA → state estimation → explained alarms → asset health |

## Sample output

All outputs below were captured from the tool.

```
$ eln info
Electrical.Net 1.0.0 — Comprehensive Electrical Engineering & Power System Framework for .NET
Framework Teknik Elektro & Sistem Tenaga Listrik Terlengkap untuk .NET
Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

Modules (23): Core, Circuit, PowerFlow, ShortCircuit, Stability, Harmonic, Protection, Renewable, SmartGrid, Optimization, StateEstimation, SCADA, IEC61850, CIM, DigitalTwin, Forecasting, AI, GIS, Visualization, Simulation, Cloud, HPC, Native
Native kernels: Rust library loaded (native (Rust))
Test cases: case9, case14, case33bw, synthetic

$ eln cases
  case9: 9 buses, 9 branches, 3 generators, 3 loads (base 100 MVA)
  case14: 14 buses, 20 branches, 5 generators, 11 loads (base 100 MVA)
  case33bw: 33 buses, 32 branches, 1 generators, 32 loads (base 10 MVA)
  synthetic100: 100 buses, 180 branches, 20 generators, 80 loads (base 100 MVA)

$ eln pf --case case9
NewtonRaphson: converged in 4 it, mismatch 1.85E-014, gen 319.641 MW, load 315 MW, losses 4.641 MW, Vmin 0.9956 pu @ bus 9

$ eln pf --case case33bw --method bfs
BackwardForwardSweep: converged in 8 it, mismatch 1.23E-009, gen 3.918 MW, load 3.715 MW, losses 0.203 MW, Vmin 0.9131 pu @ bus 18

$ eln sc --case case14 --bus 4 --type slg
SingleLineToGround fault at bus 4 (138 kV): I"k = 4.625 kA, ip = 11.234 kA, Ib = 4.625 kA, Ith = 4.682 kA, S"k = 1105.4 MVA
  κ = 1.718, R/X = 0.113, 3I0 = 4.625 kA

$ eln stability --case case9 --bus 8 --clear 0.083 --trip 8-9
Transient stability on case9: 3φ fault at bus 8, cleared at 0.083 s by tripping branch 8-9
  Result: STABLE
  Critical clearing time: 0.161 s

$ eln pv --case case14
PV curve on case14: λmax = 4.059 (margin 792.4 MW), critical bus 5
L-index: critical bus 14

$ eln arcflash --kv 0.48 --ika 30 --time 0.2
Arc flash (IEEE 1584-2002): Ia = 15.72 kA, E = 10.58 cal/cm² (44.2 J/cm²)
  Arc-flash boundary 1994 mm · PPE category 3: arc rating ≥ 25 cal/cm²

$ eln relay --pickup 400 --tms 0.1 --current 4000
IecStandardInverse, pickup 400 A, TMS 0.1: I = 4000 A (10.0× pickup) → t = 0.297 s

$ eln opf --case case9
DC-OPF on case9: optimal, cost 5216.03 $/h, λ = 24.044 $/MWh
  Gen 1:    86.56 MW
  Gen 2:   134.38 MW
  Gen 3:    94.06 MW
  LMP [$/MWh]: 1:24.04  2:24.04  3:24.04  4:24.04  5:24.04  6:24.04  7:24.04  8:24.04  9:24.04

$ eln uc
Unit commitment (24 h): feasible, production 185167 $, startup 100 $, total 185267 $
  Priority order: 2, 1, 3

$ eln se --case case14
WLS state estimation on case14: 122 measurements, converged True in 4 it
  Initial J = 68.5 (χ² passed: True); final J = 68.5 ≤ 130.0
  Max |ΔV| vs true state 0.0015 pu, max |Δθ| 0.096°

$ eln reconfig
Feeder reconfiguration (33-bus, 5 ties): open 14-15, 25-29, 32-33, 7-8, 9-10
  Losses 202.7 → 139.6 kW (31.1 %), Vmin 0.9131 → 0.9378 pu, 114 power flows

$ eln vvo
Volt/VAR optimization (33-bus): losses 202.7 → 118.6 kW, V [0.913, 1.000] → [0.995, 1.050] pu, feasible True
  Substation set-point 1.050 pu; capacitors: bus 14 300 kvar, bus 24 450 kvar, bus 30 900 kvar; inverters: bus 18 100 kvar, bus 33 100 kvar

$ eln caps
Capacitor placement (33-bus): bus 13 400 kvar, bus 24 550 kvar, bus 30 1150 kvar
  Losses 202.7 → 132.2 kW (34.8 %), Vmin 0.9131 → 0.9370 pu

$ eln hosting
PV hosting capacity at bus 18 of case33bw: 2.09 MW (limited by Overvoltage)
```

More output (`pf`, `n1`, `sc` scan, `modes`, `harmonic`, `ed`, `forecast`, `plf`, `qsts`, `ask`, `twin`) is shown in the topic guides:
[power flow](power-flow.md), [short circuit](short-circuit-protection.md), [stability](stability.md), [harmonics](harmonics.md),
[optimization](optimization.md), [renewables & smart grid](renewables-smartgrid.md), [simulation & HPC](simulation-hpc-native.md),
[AI & digital twin](ai-digital-twin.md).

## Tips

- `eln pf --json` prints the power-flow JSON used by the REST API (`NetworkJson`).
- `eln sld --literal` writes literal colours (for viewers that ignore CSS variables); `--dark` selects the dark palette.
- `eln convert --case case14 --out case14.xml` exports CIM; `--out grid.m` writes MATPOWER; `--out grid.json` writes Electrical.Net JSON.
  Combine with `--file` to convert between formats.
- `eln rtu --port 1502` serves case14 measurements over Modbus TCP until stopped (`--seconds N` to stop automatically); point the
  `eln-scada` template at it with `--rtu 127.0.0.1 --port 1502`.
- `eln ask` uses an LLM when `ELECTRICAL_LLM_PROVIDER` (or `OPENAI_API_KEY` / `HF_TOKEN`) is set; see [AI & digital twin](ai-digital-twin.md).

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/cli.md)*
