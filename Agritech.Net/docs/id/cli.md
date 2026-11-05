# CLI `agritech`

```bash
dotnet tool install -g Agritech.Cli
agritech help
```

| Perintah | Contoh |
|---|---|
| `info` | Modul, backend native, status LLM |
| `crops list` / `crops recommend` | `agritech crops recommend --temp 27 --rain 1800 --ph 5.5` |
| `soil` | `agritech soil --ph 5.4 --oc 1.2 --p 9 --clay 40 --crop rice --area 2 --target 6.5` |
| `et0` | `agritech et0 --lat -6.3 --tmin 23 --tmax 32 --rh 80` |
| `weather` | `agritech weather --lat -6.3 --lon 107.3 --days 7` |
| `ndvi` | `agritech ndvi --out ndvi.png` |
| `mission` | `agritech mission --width 300 --height 200 --alt 80 --out lahan.plan` |
| `simulate` | `agritech simulate --crop maize --irrigated --years 10` |
| `ask` | `agritech ask "Apakah sawah perlu diairi hari ini?" --crop rice --dap 30` |
| `lpp` | `agritech lpp 03670110056700FF` |
| `thi` | `agritech thi --temp 33 --rh 70` |
