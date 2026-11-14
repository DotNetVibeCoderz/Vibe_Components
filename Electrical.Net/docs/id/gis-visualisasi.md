# GIS dan visualisasi

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/gis-visualization.md)

Paket: `Electrical.GIS`, `Electrical.Visualization`, dan `Electrical.Visualization.Blazor`. Semuanya dirender menjadi string SVG, sehingga
keluaran yang sama dapat dipakai di Blazor, Avalonia (lewat kontrol SVG), file, dan laporan.

## GIS

Lokasi bus disimpan di `Bus.Location` (`GeoPoint(Latitude, Longitude)`, derajat WGS-84); `Branch.LengthKm` menyimpan panjang saluran.
`TestCases.SyntheticGrid(n)` menempatkan bus-busnya secara geografis.

```csharp
var net = TestCases.SyntheticGrid(60);                         // jaringan sintetis dengan lokasi bus
var pf = PowerFlowSolver.Solve(net);
string geojson = GeoJsonExporter.Export(net, pf);               // bus, saluran (vm, loading …) sebagai FeatureCollection
File.WriteAllText("grid.geojson", geojson);
string map = GisSvgRenderer.Render(net, pf, new GisSvgOptions { Title = "150 kV grid", ShowLegend = true });

var imported = GeoJsonImporter.Import(geojson);                 // jaringan + jalur saluran + peringatan

var plain = TestCases.Case14();
GeoLayout.ForceDirected(plain, new GeoPoint(-6.2, 106.8), new GeoLayoutOptions { RadiusKm = 30, AssignLengths = true });

var index = new BusSpatialIndex(net);
var nearest = index.Nearest(new GeoPoint(-6.21, 106.85));
var nearby = index.WithinRadius(new GeoPoint(-6.21, 106.85), radiusKm: 5);

var outage = net.Clone();
outage.Branches[3].InService = false;
var dead = OutageArea.DeEnergizedBuses(outage);
var ring = OutageArea.Polygon(outage, dead, bufferKm: 0.5);
Console.WriteLine($"{dead.Count} bus, {OutageArea.AreaKm2(ring):0.0} km²");
```

| Tipe | Kegunaan |
|---|---|
| `GeoJsonExporter` | FeatureCollection berisi titik bus (id `bus-<n>`, `kind`, `baseKv`, `type`, beban/pembangkitan, serta `vm`, `vaDeg` bila ada hasil), fitur generator, dan line string (`r`, `x`, `b`, `rateA`, `lengthKm`; `pFromMw`, `loading`, `lossMw` bila ada hasil) yang memuat semua data untuk impor tanpa kehilangan; `Write` ke stream atau `Utf8JsonWriter`; helper `Polygon` |
| `GeoJsonImporter` | Membangun ulang `PowerNetwork` dari GeoJSON, menempelkan ujung saluran ke bus dalam `snapToleranceKm`; `ApplyLocations` menyalin koordinat ke jaringan yang sudah ada |
| `GisSvgRenderer` / `GisSvgOptions` / `GisColors` | Peta SVG Web Mercator: bus diwarnai menurut pita tegangan, saluran menurut pembebanan, legenda, skala, poligon area padam dan jalur saluran opsional; memakai variabel CSS `--eln-*` |
| `GeoLayout.ForceDirected` | Penempatan force-directed deterministik untuk bus tanpa koordinat di sekitar titik pusat (lokasi yang sudah ada menjadi jangkar tetap) |
| `BusSpatialIndex` | k-d tree statis pada vektor bola satuan: `Nearest`, `KNearest`, `WithinRadius` (sesuai brute force di tes) |
| `OutageArea` | Bus yang padam setelah switching, poligon convex hull/buffer, luas (km²), point-in-polygon |
| `Geo`, `WebMercator`, `GeoBounds`, `LineLengths` | Jarak haversine, bearing, offset, proyeksi EPSG:3857, matematika piksel/tile (penomoran OSM), batas, pengisian `LengthKm` dari geometri |

CLI: `eln geojson --case synthetic60 --out grid.geojson --svg map.svg` (`--lat/--lon` menentukan pusat tata letak force-directed untuk kasus tanpa
koordinat).

![Demo GIS](../images/web-gis.png)

## Visualisasi

### Diagram segaris

`SldLayout.Compute` menempatkan bus dalam lapisan dengan BFS dari slack (atau `RootBusId`), mengurutkan bar untuk mengurangi persilangan, dan
merutekan cabang secara ortogonal; `SldRenderer.Render` menggambar bar, trafo (dua lingkaran), generator, beban, shunt, dan bila ada hasil,
bar berwarna tegangan serta cabang berwarna pembebanan dengan label aliran.

```csharp
var net = TestCases.Case14();
var pf = PowerFlowSolver.Solve(net);
string svg = SldRenderer.Render(net, pf, new SldOptions { Title = "IEEE 14-bus" });   // memakai warna var(--eln-*)
File.WriteAllText("sld.svg", SvgTheme.Resolve(svg, dark: false));                   // warna literal untuk file
```

CLI: `eln sld --case case14 --out sld.svg [--literal --dark]`.

### Grafik

```csharp
string chart = SvgCharts.Line([new ChartSeries("Vm", pf.Buses.Select(b => b.Vm).ToList())],
    new ChartOptions { Title = "Bus voltages", YLabel = "pu", XLabels = pf.Buses.Select(b => b.BusId.ToString()).ToList() });
var relay = new OvercurrentRelay("R1", 400, CurveType.IecStandardInverse, 0.1);
string tcc = SvgCharts.Tcc([TccCurve.FromFunction("R1", relay.OperatingTime, 420, 20_000)]);
string gauge = SvgCharts.Gauge(pf.MinVoltageBus.Vm, 0.9, 1.1, "Vmin", "pu");
byte[] png = PngEncoder.Encode(new byte[4 * 16 * 16], 16, 16);
```

`SvgCharts`: `Line`, `TimeSeries`, `Bar` (berkelompok/bertumpuk), `StackedArea`, `Heatmap`, `Gauge`, `Sparkline`, `KpiTile`, `Phasor`, `Polar`,
dan `Tcc` (dekade log-log). `ColorRamp` (tegangan, pembebanan, viridis, thermal), `PngEncoder` (RGBA → PNG, data URI), dan `Raster` untuk gambar
heatmap.

### Tema

Semua SVG memakai variabel CSS (`--eln-*`) dengan nilai terang dan gelap (`SvgTheme.Variables`). Di halaman web sertakan `SvgTheme.Stylesheet`
(komponen Blazor `ElectricalTheme` melakukannya); untuk file atau renderer non-browser panggil `SvgTheme.Resolve(svg, dark)` untuk mengganti
dengan warna literal. Mode gelap mengikuti `prefers-color-scheme` atau `data-theme="dark"`.

### Komponen Blazor

```razor
@using Electrical.Visualization.Blazor

<ElectricalTheme />
<KpiCard Label="Losses" Value="@result.LossesMw.ToString("0.00")" Unit="MW" />
<SingleLineDiagram Network="net" Result="result" Caption="Single-line diagram with power-flow overlay" />
```

Komponen: `ElectricalTheme`, `SingleLineDiagram`, `LineChart`, `BarChart`, `Heatmap`, `PhasorDiagram`, `TccChart`, `Gauge`, `KpiCard`, `SvgMarkup`
(menampung string SVG apa pun). Lihat templat `eln-blazor` untuk dasbor lengkap.

DI: `AddVisualization()` mendaftarkan `IDiagramRenderer` (`SingleLine`, `Resolve`); `AddGis()` mendaftarkan `IGisService`.

![Demo aliran daya](../images/web-powerflow.png)

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/gis-visualization.md)*
