# GIS and visualization

> Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/gis-visualisasi.md)

Packages: `Electrical.GIS`, `Electrical.Visualization` and `Electrical.Visualization.Blazor`. Everything renders to SVG strings, so the same
output works in Blazor, Avalonia (via an SVG control), files and reports.

## GIS

Bus locations are `Bus.Location` (`GeoPoint(Latitude, Longitude)`, WGS-84 degrees); `Branch.LengthKm` holds line lengths.
`TestCases.SyntheticGrid(n)` places its buses geographically.

```csharp
var net = TestCases.SyntheticGrid(60);                         // synthetic grid with bus locations
var pf = PowerFlowSolver.Solve(net);
string geojson = GeoJsonExporter.Export(net, pf);               // buses, lines (vm, loading …) as FeatureCollection
File.WriteAllText("grid.geojson", geojson);
string map = GisSvgRenderer.Render(net, pf, new GisSvgOptions { Title = "150 kV grid", ShowLegend = true });

var imported = GeoJsonImporter.Import(geojson);                 // network + line paths + warnings

var plain = TestCases.Case14();
GeoLayout.ForceDirected(plain, new GeoPoint(-6.2, 106.8), new GeoLayoutOptions { RadiusKm = 30, AssignLengths = true });

var index = new BusSpatialIndex(net);
var nearest = index.Nearest(new GeoPoint(-6.21, 106.85));
var nearby = index.WithinRadius(new GeoPoint(-6.21, 106.85), radiusKm: 5);

var outage = net.Clone();
outage.Branches[3].InService = false;
var dead = OutageArea.DeEnergizedBuses(outage);
var ring = OutageArea.Polygon(outage, dead, bufferKm: 0.5);
Console.WriteLine($"{dead.Count} buses, {OutageArea.AreaKm2(ring):0.0} km²");
```

| Type | Purpose |
|---|---|
| `GeoJsonExporter` | FeatureCollection with bus points (id `bus-<n>`, `kind`, `baseKv`, `type`, load/generation, and `vm`, `vaDeg` with a result), generator features and line strings (`r`, `x`, `b`, `rateA`, `lengthKm`; `pFromMw`, `loading`, `lossMw` with a result) carrying everything needed for a lossless import; `Write` to a stream or `Utf8JsonWriter`; `Polygon` helper |
| `GeoJsonImporter` | Rebuilds a `PowerNetwork` from GeoJSON, snapping line ends to buses within `snapToleranceKm`; `ApplyLocations` copies coordinates onto an existing network |
| `GisSvgRenderer` / `GisSvgOptions` / `GisColors` | Web Mercator SVG map: buses coloured by voltage band, lines by loading, legend, scale bar, optional outage polygon and line paths; uses `--eln-*` CSS variables |
| `GeoLayout.ForceDirected` | Deterministic force-directed placement of buses without coordinates around a centre (existing locations are fixed anchors) |
| `BusSpatialIndex` | Static k-d tree on unit-sphere vectors: `Nearest`, `KNearest`, `WithinRadius` (matches brute force in tests) |
| `OutageArea` | De-energised buses after switching, convex-hull/buffer polygon, area (km²), point-in-polygon |
| `Geo`, `WebMercator`, `GeoBounds`, `LineLengths` | Haversine distance, bearing, offsets, EPSG:3857 projection, pixel/tile maths (OSM numbering), bounds, assigning `LengthKm` from geometry |

CLI: `eln geojson --case synthetic60 --out grid.geojson --svg map.svg` (`--lat/--lon` centre the force-directed layout for cases without
coordinates).

![GIS demo](../images/web-gis.png)

## Visualization

### Single-line diagrams

`SldLayout.Compute` places buses in layers by BFS from the slack (or `RootBusId`), orders bars to reduce crossings and routes branches
orthogonally; `SldRenderer.Render` draws bars, transformers (two circles), generators, loads, shunts and, with a result, voltage-coloured bars
and loading-coloured branches with flow labels.

```csharp
var net = TestCases.Case14();
var pf = PowerFlowSolver.Solve(net);
string svg = SldRenderer.Render(net, pf, new SldOptions { Title = "IEEE 14-bus" });   // uses var(--eln-*) colours
File.WriteAllText("sld.svg", SvgTheme.Resolve(svg, dark: false));                   // literal colours for files
```

CLI: `eln sld --case case14 --out sld.svg [--literal --dark]`.

### Charts

```csharp
string chart = SvgCharts.Line([new ChartSeries("Vm", pf.Buses.Select(b => b.Vm).ToList())],
    new ChartOptions { Title = "Bus voltages", YLabel = "pu", XLabels = pf.Buses.Select(b => b.BusId.ToString()).ToList() });
var relay = new OvercurrentRelay("R1", 400, CurveType.IecStandardInverse, 0.1);
string tcc = SvgCharts.Tcc([TccCurve.FromFunction("R1", relay.OperatingTime, 420, 20_000)]);
string gauge = SvgCharts.Gauge(pf.MinVoltageBus.Vm, 0.9, 1.1, "Vmin", "pu");
byte[] png = PngEncoder.Encode(new byte[4 * 16 * 16], 16, 16);
```

`SvgCharts`: `Line`, `TimeSeries`, `Bar` (grouped/stacked), `StackedArea`, `Heatmap`, `Gauge`, `Sparkline`, `KpiTile`, `Phasor`, `Polar`
and `Tcc` (log-log decades). `ColorRamp` (voltage, loading, viridis, thermal), `PngEncoder` (RGBA → PNG, data URIs) and `Raster` for
heatmap images.

### Theming

All SVG uses CSS variables (`--eln-*`) with light and dark values (`SvgTheme.Variables`). In a web page include `SvgTheme.Stylesheet` (the
Blazor `ElectricalTheme` component does it); for files or non-browser renderers call `SvgTheme.Resolve(svg, dark)` to substitute literal
colours. Dark mode follows `prefers-color-scheme` or `data-theme="dark"`.

### Blazor components

```razor
@using Electrical.Visualization.Blazor

<ElectricalTheme />
<KpiCard Label="Losses" Value="@result.LossesMw.ToString("0.00")" Unit="MW" />
<SingleLineDiagram Network="net" Result="result" Caption="Single-line diagram with power-flow overlay" />
```

Components: `ElectricalTheme`, `SingleLineDiagram`, `LineChart`, `BarChart`, `Heatmap`, `PhasorDiagram`, `TccChart`, `Gauge`, `KpiCard`,
`SvgMarkup` (hosts any SVG string). See the `eln-blazor` template for a complete dashboard.

DI: `AddVisualization()` registers `IDiagramRenderer` (`SingleLine`, `Resolve`); `AddGis()` registers `IGisService`.

![Power flow demo](../images/web-powerflow.png)

---
*Made by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](../id/gis-visualisasi.md)*
