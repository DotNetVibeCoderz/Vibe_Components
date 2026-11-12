# CLI: `aqua`

```bash
dotnet tool install -g AquaCulture.Cli
aqua help
```

| Command | Example |
|---|---|
| `info` | platform, native backend, configured LLM |
| `species` | `aqua species --id kerapu` |
| `wq` | `aqua wq --species vaname --temp 30 --do 3.8 --ph 8.4 --tan 1.6 --sal 18 --volume 2600 --biomass 3000 --lang id` |
| `feed` | `aqua feed --species nila --abw 120 --pop 8000 --temp 28 --do 4` |
| `grow` | `aqua grow --species patin --abw 10 --pop 20000 --target 1000 --sims 1000` |
| `oxygen` | `aqua oxygen --biomass 3500 --kw 6 --setpoint 4.5` |
| `diagnose` | `aqua diagnose --species vaname --signs white_spots_shell,red_body --temp 26` (omit `--signs` to list them) |
| `msy` | `aqua msy --csv catch.csv` (`year,catch,effort`; built-in demo data without `--csv`) |
| `ypr` | `aqua ypr --winf 2000 --k 0.3 --t0 -0.2 --m 0.4 --tr 0.5 --tc 1.5` |
| `weather` | `aqua weather --lat -6.33 --lon 108.32 --days 3 --sal 18` (Open-Meteo; `--offline` for synthetic) |
| `modbus` | `aqua modbus 07 03 0C 02 00 01 23 03 11 FF 88 00 B6 01 57 24 CD` |
| `lora` | `aqua lora 021E0B3B0313009B03480F50` |
| `ras` | `aqua ras --feed 10 --protein 40 --volume 20` |
| `audit` | `aqua audit --standard asc --lang id` |
| `report` | `aqua report --format pdf --out cycle.pdf` |
| `ask` | `aqua ask "Kapan bisa panen di 20 g?"` (uses `AQUA_LLM_*`) |
