# Templat

```bash
dotnet new install AquaCulture.Templates
```

| Nama singkat | Proyek |
|---|---|
| `aqua-console` | Penasihat kolam: kualitas air, rencana pakan, prakiraan panen dan AquaAgent |
| `aqua-blazor` | Dasbor tambak dengan sensor simulasi, `SensorGrid`, `FeedScheduler` dan `DigitalTwinViewer` 3D |
| `aqua-iot-edge` | Worker service untuk Raspberry Pi / PC industri: MQTT (atau simulator) → `FarmTwin` → perintah kincir |
| `aqua-avalonia` | Monitor kolam desktop dengan WQI, grafik DO dan keputusan |

Semua templat menerima `--species` (nila, lele, gurame, patin, koi, bandeng, kerapu, kakap, vaname, windu).

```bash
dotnet new aqua-blazor -n DasborTambak --species vaname
```
