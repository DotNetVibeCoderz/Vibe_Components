# CLI: `aqua`

```bash
dotnet tool install -g AquaCulture.Cli
aqua help
```

Tambahkan `--lang id` untuk keluaran Bahasa Indonesia.

| Perintah | Contoh |
|---|---|
| `info` | platform, kernel native, LLM yang dikonfigurasi |
| `species` | `aqua species --id kerapu --lang id` |
| `wq` | `aqua wq --species vaname --temp 30 --do 3.8 --ph 8.4 --tan 1.6 --sal 18 --lang id` |
| `feed` | `aqua feed --species nila --abw 120 --pop 8000 --temp 28 --do 4` |
| `grow` | `aqua grow --species patin --target 1000` |
| `oxygen` | `aqua oxygen --biomass 3500 --kw 6` |
| `diagnose` | `aqua diagnose --species vaname --signs white_spots_shell,red_body` |
| `msy` / `ypr` | kajian stok |
| `weather` | `aqua weather --lat -6.33 --lon 108.32 --sal 18` (Open-Meteo) |
| `modbus` / `lora` | dekode frame sensor |
| `ras` | neraca massa RAS |
| `audit` | `aqua audit --standard cbib --lang id` |
| `report` | `aqua report --format xlsx --out siklus.xlsx --lang id` |
| `ask` | `aqua ask "Berapa pakan hari ini?"` |
