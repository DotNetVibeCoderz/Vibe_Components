# Templat proyek

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/templates.md)

```bash
dotnet new install Electrical.Templates
dotnet new list eln
```

| Nama pendek | Templat | Paket | Parameter |
|---|---|---|---|
| `eln-console` | Studi sistem tenaga (konsol): aliran daya dengan batas Q, peringkat N-1, pemindaian IEC 60909, aliran daya probabilistik, SVG diagram segaris | Electrical.HPC, .ShortCircuit, .Visualization | `--case case9\|case14\|case33bw` (default case14) |
| `eln-blazor` | Dasbor jaringan Blazor Server: diagram segaris langsung, kartu KPI, dan slider what-if skala beban | Electrical.Visualization.Blazor | `--case` |
| `eln-avalonia` | Penampil diagram segaris desktop Avalonia dengan overlay aliran daya | Electrical.Visualization, Avalonia 12, Svg.Controls.Skia.Avalonia | `--case` |
| `eln-scada` | Gateway edge SCADA: membaca RTU Modbus TCP, menskalakan tag, mengevaluasi alarm ISA-18.2, dan menyimpan histori (simulator RTU bawaan bila RTU tidak diberikan) | Electrical.SCADA | – |
| `eln-webapi` | Minimal API ASP.NET Core untuk aliran daya, kontingensi, hubung singkat, dan job studi | Electrical.Cloud | – |

```bash
dotnet new eln-console -n StudiJaringan --case case33bw
cd StudiJaringan && dotnet run      # mencetak hasil, menulis single-line.svg

dotnet new eln-blazor -n DasborJaringan
dotnet new eln-avalonia -n PenampilJaringan --case case9
dotnet new eln-scada -n GatewayScada
dotnet run --project GatewayScada -- --rtu 127.0.0.1 --port 1502   # baca RTU sungguhan (atau `eln rtu --port 1502`)
dotnet new eln-webapi -n ApiJaringan
```

## Isi setiap templat

**eln-console** (`Program.cs`): memuat `TestCases.ByName("<case>")` (ganti dengan `Matpower.Load("kasusku.m")`), menyelesaikan aliran daya AC
dengan `EnforceQLimits`, mencetak hasil bus, mengurutkan lima gangguan N-1 terburuk, mencetak pemindaian IEC 60909, menjalankan aliran daya
probabilistik 300 sampel (melaporkan backend native/managed), dan menyimpan `single-line.svg` dengan
`SvgTheme.Resolve(SldRenderer.Render(net, pf), dark: false)`.

**eln-blazor**: aplikasi Blazor Server dengan `Dashboard.razor` yang memakai `KpiCard` dan `SingleLineDiagram` dari
`Electrical.Visualization.Blazor`; menggeser slider akan menskalakan semua beban dan menghitung ulang aliran daya.

**eln-avalonia**: jendela desktop yang merender SVG diagram segaris lewat `Svg.Controls.Skia.Avalonia`.

**eln-scada**: menjalankan `RtuSimulator` (IEEE 14-bus) pada `ModbusTcpServer` loopback kecuali `--rtu <host> --port <port>` diberikan, membuat
`TagDatabase`, `AlarmManager` (HH 1,10 / H 1,06 / L 0,95 / LL 0,90 pu pada tegangan bus), dan `Historian`, lalu memindai lima siklus serta
mencetak alarm dan tren.

**eln-webapi**: `WebApplication.CreateSlimBuilder` + `AddElectrical(b => b.AddPowerFlow().AddCloud(workers: 2))` + `MapElectricalApi()`;
coba `curl -X POST http://localhost:5000/api/electrical/studies/powerflow -H "Content-Type: application/json" -d '{"case":"case14"}'`
(gunakan URL yang dicetak oleh `dotnet run`). Lihat [API cloud](api-cloud.md).

Setiap proyek yang dihasilkan membawa kredit "built with Electrical.Net (Gravicode Studios, led by Kang Fadhil)".

## Membangun paket templat

Templat berada di `templates/Electrical.Templates/content/<nama-pendek>` dengan `.template.config/template.json` (`sourceName` GridViewer,
GridDashboard, ElectricalStudyApp, ScadaGateway, GridApi). `pwsh build/pack.ps1` mengemasnya sebagai `Electrical.Templates`; pasang build lokal
dengan `dotnet new install artifacts/packages/Electrical.Templates.1.0.0.nupkg`. Proyek yang dihasilkan mereferensikan paket NuGet
`Electrical.*` versi 1.0.0.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/templates.md)*
