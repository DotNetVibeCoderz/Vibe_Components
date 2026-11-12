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
| `bmkg` | `aqua bmkg --adm4 31.71.03.1001 --lang id` (prakiraan BMKG per desa dan dampaknya ke kolam) |
| `history` | `aqua history --lat -6.33 --lon 108.32 --start 2015-01-01 --end 2024-12-31 --lang id` (NASA POWER → risiko iklim) |
| `marine` | `aqua marine --lat -5.85 --lon 106.6 --species kerapu` (gelombang, arus, suhu laut untuk KJA) |
| `export` | `aqua export --db aqua.db --pond P-01 --out readings.parquet` (bisa `.csv`; `--demo` mengisi data simulasi sehari) |
| `opcua` | `aqua opcua --seconds 30` (simulator kolam OPC UA dan kliennya) |
| `modbus` / `lora` | dekode frame sensor |
| `ras` | neraca massa RAS |
| `audit` | `aqua audit --standard cbib --lang id` |
| `report` | `aqua report --format xlsx --out siklus.xlsx --lang id` |
| `ask` | `aqua ask "Berapa pakan hari ini?"` |
