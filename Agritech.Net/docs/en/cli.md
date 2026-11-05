# CLI: `agritech`

```bash
dotnet tool install -g Agritech.Cli
agritech help
```

| Command | Example |
|---|---|
| `info` | Modules, native backend, LLM status |
| `crops list` | Crop catalog |
| `crops recommend` | `agritech crops recommend --temp 27 --rain 1800 --ph 5.5 --elev 300 --irrigated` |
| `soil` | `agritech soil --ph 5.4 --oc 1.2 --n 0.12 --p 9 --k 0.28 --sand 22 --silt 38 --clay 40 --crop rice --area 2 --target 6.5` |
| `et0` | `agritech et0 --lat -6.3 --tmin 23 --tmax 32 --rh 80 --wind 2 --rs 20` |
| `weather` | `agritech weather --lat -6.3 --lon 107.3 --days 7` (Open-Meteo; `--offline` for synthetic) |
| `ndvi` | `agritech ndvi --seed 7 --out ndvi.png` |
| `mission` | `agritech mission --lat -6.3 --lon 107.3 --width 300 --height 200 --alt 80 --out field.plan` or `--field field.geojson` |
| `simulate` | `agritech simulate --crop maize --planting 2026-04-01 --irrigated --n 120 --years 10` |
| `ask` | `agritech ask "Berapa dosis pupuk terbaik minggu ini?" --crop rice --dap 20 --area 2 [--live-weather]` |
| `lpp` | `agritech lpp 03670110056700FF` |
| `thi` | `agritech thi --temp 33 --rh 70` |

Example output:

```
$ agritech thi --temp 33 --rh 70
THI 85.9 → Moderate; estimated milk loss 2.8 kg/cow/day
```
