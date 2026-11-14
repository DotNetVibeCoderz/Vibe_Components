# Electrical.Net: progress tracking

> Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

Status legend: ✅ done · 🟡 partial · ⏳ planned

| Area | Status | Notes / validation |
|---|---|---|
| Solution scaffold (.NET 10, slnx, shared props) | ✅ | Agritech.Net layout; AOT-compatible libraries |
| Electrical.Core | ✅ | Sparse LU (min-degree, IKJ), dense LU, network model, Y-bus (±0 seq), MATPOWER I/O, IEEE cases, per-unit, symmetrical components |
| Electrical.PowerFlow | ✅ | NR / FDLF / GS / DC / BFS, Q limits, ZIP loads, N-1, PTDF/LODF. case9, case14 (13.393 MW), 33-bus (202.7 kW) match references |
| Electrical.Circuit | ✅ | MNA DC/AC/transient, SPICE-like netlists, diodes, transformers, three-phase |
| Electrical.ShortCircuit | ✅ | IEC 60909 3φ/SLG/LL/LLG, κ (methods B/C), Ib, Ith, external grids |
| Electrical.Stability | ✅ | Classical TS + CCT (case9 0.162 s), eigenvalues match Sauer & Pai, PV/QV curves, L-index, frequency response |
| Electrical.Dynamics (v1.3) | ✅ | RMS DAE engine (implicit trapezoidal + modified Euler), GENCLS/GENROU/GENSAL, SEXS/IEEET1/ESST1A/ESAC4A, TGOV1/HYGOV/GAST/IEEEG1, IEEEST/PSS2B, REGC_A/REEC_A/REPC_A, grid-forming droop/VSM, .dyr reader, small-signal of the detailed model; Kundur two-area modes 0.54/1.08/1.11 Hz |
| Electrical.Harmonic | ✅ | FFT/Bluestein, THD/TDD/K-factor, IEEE 519, IEC 61000, harmonic load flow, frequency scan, filter design |
| Electrical.Protection | ✅ | IEC/IEEE curves, grading, distance, differential, breaker duty, arc flash (IEEE 1584-2002; 2018 model ⏳) |
| Electrical.Renewable | ✅ | NREL SPA check, single-diode (Villalva KC200GT), PVWatts, wind/Weibull AEP, BESS, IEEE 1547 curves, hosting capacity |
| Electrical.SmartGrid | ✅ | Reconfiguration reaches 139.55 kW optimum, VVO, EV valley filling, microgrid DP, VPP, reliability (Billinton & Allan) |
| Electrical.Optimization | ✅ | ED (case9 λ 24.04), DC-OPF with LMPs, SCOPF, UC, capacitor placement; v1.2: AC-OPF (= MATPOWER runopf case9 5296.69 / case14 8081.53 $/h), LMP decomposition, LP/MILP (`LinearModel`, dual simplex, branch-and-bound) |
| LP/MILP abstraction (v1.2) | ✅ | `LinearModel`, `ILpSolver`/`IMilpSolver`; built-in bounded dual simplex (duals, reduced costs, Bland fallback) + branch and bound (pseudo-costs, best-bound/dive); Wyndor/Beale/knapsack brute-force checks |
| Electrical.Optimization.Highs / .OrTools (v1.2) | ✅ | HiGHS 1.16.0 (`Highs.Native`, native lib re-shipped as `highs-native.*` to avoid the OR-Tools `highs.dll` clash) and OR-Tools 9.15.6755 (SCIP/GLOP); same optima and LP duals as built-in |
| Electrical.Markets (v1.2) | ✅ | SCUC MILP (min up/down, ramps, reserve, PTDF + LODF N-1, lazy constraints) with fixed-commitment LMPs; W&W 24 h: 183 665 $ vs priority-list DP 184 368 $; DC LMP decomposition, settlement, FTR revenue adequacy, two-bus hand check |
| Electrical.StateEstimation | ✅ | WLS, observability, χ² + largest normalized residual bad-data removal |
| Electrical.Forecasting | ✅ | Holt-Winters (double seasonal), regression, similar-day, ensemble, solar/wind forecasts |
| Electrical.Simulation | ✅ | QSTS, events, scenarios, sequential Monte-Carlo adequacy (LOLE/EENS); v1.1: OLTC, LDC regulator and switched-capacitor controllers in QSTS (Case33 demo: 64 → 1 violation steps) |
| Electrical.Interop (v1.1) | ✅ | PSS/E RAW v33/v35 read/write (3-winding → star, switched shunt → fixed), IEEE CDF reader, format auto-detection; IEEE 14 RAW/CDF reproduce case14 (13.393 MW) |
| Electrical.SCADA | ✅ | Modbus TCP client/server, tags, ISA-18.2 alarms, historian (swinging door), RTU simulator, IEC 104 codec |
| Electrical.IEC61850 | ✅ | SCL parser, BER, GOOSE, Sampled Values 9-2LE, LN catalog |
| Electrical.CIM | ✅ | CGMES-style RDF/XML export/import; lossless round trip |
| Electrical.GIS | ✅ | GeoJSON, Web Mercator, k-d tree, force-directed layout, outage polygons, SVG maps |
| Electrical.Visualization (+ Blazor) | ✅ | Auto single-line diagrams, SVG charts (TCC, phasor, heatmap…), PNG encoder, themeable CSS variables |
| Electrical.Cloud | ✅ | JSON contracts, study runner with queue, scenario stores, minimal API (`MapElectricalApi`) |
| Electrical.HPC | ✅ | Parallel batch/sweep, Monte-Carlo probabilistic load flow, benchmark |
| Electrical.Native (Rust) | ✅ | FFT, Goertzel harmonics, sparse complex injections, SplitMix64/Box–Muller; identical managed fallback |
| Electrical.DigitalTwin | ✅ | Live twin (SCADA → WLS SE → explained EN/ID alarms), what-if, IEEE C57.91 asset ageing, deterministic TwinSimulator |
| Electrical.AI | ✅ | GridAgent with 10 tools (live-tested on Azure OpenAI gpt-5-mini; offline EN/ID rule-based), anomaly detection, fault classification/location, clustering, Volt/VAR RL environment + Q-learning |
| Electrical.Distribution (v1.1) | ✅ | Unbalanced 3φ power flow, Carson/Kron (Kersting Ex. 4.1–4.3, 5.1), regulators/OLTC/capacitor controllers, OpenDSS reader; IEEE 13-node worst error 0.148 %, IEEE 4-node 0.026 % |
| Tests | ✅ | 258 xUnit tests passing, 1 live-LLM test skipped by default (+ 5 Rust tests) |
| CLI (`eln`) | ✅ | 34 commands: power flow, faults, stability, dispatch, SE, forecasting, QSTS, SCADA RTU, REST server, AI agent, twin; v1.1 adds `dist`, `dss`, `linecode`, `.raw`/`.cdf` input and `qsts --control` |
| Templates | ✅ | 6 templates (console, distribution feeder, Blazor, Avalonia, SCADA gateway, Web API) |
| NuGet icon & packages | ✅ | 28 packages (+ symbols) pack without warnings; icon in `assets/` |
| Gallery: Blazor / Avalonia | ✅ | 20+ demos, EN/ID, light/dark, screenshots in docs/images (web + desktop) |
| Docs EN / ID | ✅ | README / README.id + 19 guides each; all code samples compile-checked |
| Monorepo move + CI workflow | ✅ | `Vibe_Components/Electrical.Net` + `.github/workflows/electrical-net.yml` (tag `electrical-net-v*` publishes with `NUGET_API_KEY`) |

## Log
- **2026-11-14**: v1.0.0 foundation. Performance fixes during verification: numeric kernels marked `AggressiveOptimization`
  (tier-0 JIT was dominating), bucket-queue minimum degree, bus-graph ordering expanded to the Jacobian, own binary heap in sparse LU.
  Rodin image generation was unavailable, so the icon is a hand-made SVG rendered with headless Chrome.
- **2026-11-14**: DigitalTwin, AI, gallery and docs finished; whole solution builds with 0 warnings. Fixed during verification: the gallery
  microgrid demo compared the DP optimum (forced to end at the initial SoC) with a rule-based run allowed to drain the battery — both now
  have the same end-of-day freedom (DP $447 vs rule-based $470).
- **2026-11-14**: v1.1 Interop module (PSS/E RAW, IEEE CDF, `NetworkFormats`) and QSTS control loops (`OltcController`, `VoltageRegulatorController`, `CapacitorController`).
- **2026-11-14**: v1.1 Electrical.Distribution (unbalanced three-phase feeders, Carson/Kron, regulators, OpenDSS) validated against IEEE 4/13-node
  and Kersting. Fixed: Newton–Raphson from a flat start diverged (30°) or converged to a spurious solution (45°) with large phase shifters;
  the flat start now takes its angles from a DC power flow (regression test). CI actions bumped to checkout@v7 / upload-artifact@v7 /
  download-artifact@v8 / setup-dotnet@v6. New template `eln-distribution`. Version 1.1.0.
  Also fixed: the reference shunt that keeps delta windings non-singular was a real conductance scaled by the winding admittance, so stiff
  substation transformers absorbed spurious power (IEEE 13-node script: 131.8 → 111.2 kW losses, matching the code-built feeder); it is now a
  tiny pure susceptance (regression test).
- **2026-11-14**: v1.2 LP/MILP abstraction, built-in dual simplex / branch and bound, HiGHS and OR-Tools adapters, Electrical.Markets (SCUC,
  day-ahead / real-time clearing, DC LMP components, settlement, FTRs). Found while verifying: `Highs.Native` and `Google.OrTools.runtime.*`
  both ship `runtimes/<rid>/native/highs.dll` (different HiGHS builds) and overwrite each other in one output folder, so OR-Tools failed to
  load; the HiGHS adapter now excludes the package's native asset, re-ships it as `highs-native.*` and loads it with a DllImport resolver.
  25 tests (`Optimization/MilpTests`, `Markets/*`), ~12 s.
- **2026-11-15**: v1.2 AC-OPF (interior point, exact Hessians) and LMP decomposition. Fixed: DC-OPF stalled on large grids (SyntheticGrid(300):
  100 iterations, 205 062 $/h instead of 186 364 $/h) because the interior point started at x = 0 — now Pmax-proportional start with
  central-path slacks/duals, 5–8 iterations up to 600 buses (regression test). Found: the built-in branch-and-bound is weak on UC
  relaxations (case9 24 h: 18.6 % gap after 20 s vs HiGHS optimal in ~1 s) — `eln scuc` defaults to HiGHS, docs steer SCUC to HiGHS/OR-Tools.
  `MarketSettlement.ElasticConsumerSurplus` added (VoLL-valued fixed demand dominated the welfare figure). CLI `acopf`, `scuc`, `market`.
  Version 1.2.0.
- **2026-11-15**: v1.3 Electrical.Emt (Dommel nodal EMT: trapezoidal companion models with CDA, switches/diodes/thyristors, sources,
  Bergeron lines with lumped losses and Clarke modes, π sections, coupled RL, transformers, three-phase helpers, `AddEmt()`). Rust kernels
  `eln_emt_branch_update` / `eln_emt_lu_solve` (bit-identical managed fallback in `EmtKernels`), `eln_version` 10300,
  `SparseLU<T>.ExportFactors()`. Validation: RL/RC/RLC analytic, cap inrush, back-to-back (3154 A vs 3162 A, 10.066 kHz), Bergeron
  doubling/reflections, 150 km energization 1.98 pu, CDA, steady state vs long-line phasor 0.001 % and vs Circuit AC 0.002 %.
  108-node 3-phase grid, Δt = 50 µs, 0.2 s: ~0.2 s (native ≈ managed; kernels are not the bottleneck). 22 tests (`tests/.../Emt`), ~2 s.
- **2026-11-15**: v1.3 Electrical.Dynamics. DAE ẋ = f(x, V), 0 = I(x, V) − Y·V with Norton units; implicit trapezoidal (Newton on [x; V],
  sparse Jacobian from exact Y plus per-unit numerical blocks, swapped Im/Re network rows so the diagonal is ±B, reused across steps) and
  explicit modified Euler. Kundur two-area (Example 12.6) load flow matches the textbook (185/235/176/202 Mvar, 20.2°/10.5°/−6.8°/−17.0°);
  constant Efd: inter-area 0.542 Hz ζ 3.8 %, local 1.077/1.112 Hz (Kundur 0.545, 1.09/1.12 Hz); ESST1A KA = 200: inter-area ζ ≈ 0.0 %;
  with Kundur's PSS ζ = 18.6 %. 10 s Kundur run ≈ 85 ms. Found while verifying: a Newton step stalls where a non-windup limit is first
  reached (clamped rows now count as satisfied) and at current-limit switching (failed steps redone with two explicit half steps,
  `FallbackSteps`); REPC_A voltage-control default gains limit-cycle in Q-control mode (documented). 31 tests (`tests/.../Dynamics`), ~2 s.
- **2026-11-15**: CLI `dyn` (Kundur / case9 fault simulation + electromechanical modes) and `emt` (line energization, capacitor inrush);
  gallery demos and EN/ID docs for dynamics, IBR and EMT. Version 1.3.0.
