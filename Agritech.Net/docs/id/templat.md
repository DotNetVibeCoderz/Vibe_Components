# Templat proyek

```bash
dotnet new install Agritech.Templates
```

| Templat | Perintah | Hasil |
|---|---|---|
| Penasihat kebun (konsol) | `dotnet new agritech-console -n PenasihatKu --crop rice` | Kesehatan tanah, jadwal pupuk, irigasi, dan agen AI |
| Dasbor kebun (Blazor) | `dotnet new agritech-blazor -n DasborKu` | Peta NDVI, grafik LAI dan air tanah, kartu KPI |
| Gerbang IoT edge | `dotnet new agritech-iot-edge -n GerbangKu --broker mqtt.lokal` | Worker service: MQTT/simulasi → analitik edge → kembaran digital → log keputusan |
| Penampil lahan (Avalonia) | `dotnet new agritech-avalonia -n PenampilKu` | Aplikasi desktop lintas platform untuk peta NDVI |
