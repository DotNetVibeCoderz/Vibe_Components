# Engineering, BIM & spatial econometrics

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil · [🇮🇩 Bahasa Indonesia](../id/engineering.md)

Phase 4 (v0.4) adds four engineering workloads:

- **Utility networks:** EPANET-compatible hydraulics, topology rules and valve isolation (in `GeoPandas.Net.Network`).
- **Civil design:** alignments, corridors, earthwork and parcel subdivision (`GeoPandas.Net.Civil`).
- **BIM:** IFC ⇄ GIS (`GeoPandas.Net.BIM`).
- **CAD:** DWG and DGN (`GeoPandas.Net.CAD`).

It also adds spatial regression and GWR to `GeoPandas.Net.Analytics`.

Each result on this page was checked against an independent implementation, and the reference values are in `tests/GeoPandas.Net.Tests/data`:

| Feature | Reference | Agreement |
|---|---|---|
| OLS, LM diagnostics, ML lag / error | PySAL spreg 1.9 | 1e-9 (OLS), 1e-6 (ML) |
| GWR (bandwidth, local estimates, AICc) | PySAL mgwr 2.2 | same bandwidth; estimates to 1e-15 |
| Universal kriging | PyKrige 1.7 | 1e-12 |
| Hydraulics (Net1, Net3, SI Darcy-Weisbach) | EPANET 2.2 (via WNTR) | heads within 0.05 mm; identical link status |
| IFC geometry, georeferencing, properties | IfcOpenShell 0.9 (OpenCascade) | 1e-15 m |
| DWG | ezdxf on the same drawing as DXF | identical entity counts, text; coordinates 2e-13 |
| DGN v7 | GDAL 3.12, both directions | exact (integer coordinates) |

## Spatial regression

```csharp
var w   = SpatialWeights.Queen(districts).RowStandardize();
var ols = SpatialRegression.Ols(districts, "income", ["green_ratio", "density"], w);
Console.WriteLine(ols);                 // coefficients, R², AIC + LM-lag, LM-error, robust LM, SARMA
var lag = SpatialRegression.SpatialLag(districts, "income", ["green_ratio", "density"], w);   // ρ, z, log L
var err = SpatialRegression.SpatialError(districts, "income", ["green_ratio", "density"], w); // λ
lag.Coefficients();                      // DataFrame: variable, coefficient, std_error, z, p_value
```

- **Estimation:** the ML models use the concentrated likelihood with log|I − ρW| from the eigenvalues of W (symmetric or row-standardised symmetric weights); any other W uses an LU decomposition. Standard errors come from the full information matrix, as spreg's `method="full"` does.
- **Size limit:** the ML models build dense n × n matrices, so they're meant for up to a few thousand observations.
- **AIC convention:** as in GeoDa and spreg, the error model's AIC counts the β only (not λ).

## Geographically weighted regression

```csharp
var gwr = Gwr.Fit(points, "y", ["x1", "x2"]);                       // adaptive bisquare, AICc golden-section bandwidth
var gwrFixed = Gwr.Fit(points, "y", ["x1", "x2"], kernel: GwrKernel.Gaussian, fixedBandwidth: true);
gwr.AddTo(points).ToFile("gwr.gpkg");   // b_x1, se_x1, t_x1, local_r2, predicted, residual per location
Console.WriteLine(gwr);                 // bandwidth, R², adj. R², AICc, ENP (tr S), σ²
```

## Universal kriging

```csharp
var vg = new Variogram(VariogramModel.Spherical, Nugget: 0.5, Sill: 3.5, Range: 300);    // of the detrended residuals
var (z, variance) = Interpolation.UniversalKriging(sx, sy, sz, qx, qy, vg, KrigingDrift.Linear);
```

`KrigingDrift.Quadratic` adds x², xy and y² terms. Without a variogram, one is fitted to the residuals of an OLS trend surface. `neighbors:` switches to a local neighbourhood.

## Utility networks

### Hydraulics (EPANET-compatible)

```csharp
var net = WaterNetwork.ReadInp("city.inp");           // or WaterNetwork.FromGeoDataFrames(pipes, junctions, reservoirs)
var result = Hydraulics.Solve(net);                   // steady state, global gradient algorithm (Todini & Pilati, as EPANET 2)
result.NodesFrame().ToFile("pressures.gpkg");         // head, pressure, demand, disconnected
result.LinksFrame().ToFile("flows.gpkg");             // flow, velocity, head loss, status
var outage = Hydraulics.Solve(net, new HydraulicOptions { ClosedLinks = ["P-17", "P-18"] });
net.WriteInp("edited.inp");                           // round trip back to EPANET
```

**Supported:**

- Hazen-Williams, Darcy-Weisbach (EPANET's friction factors) and Chezy-Manning head loss, with minor losses.
- Check valves.
- Pumps: one-point, three-point and multi-point curves, constant power, and speed.
- Open, closed and throttle-control valves.
- Patterns at a chosen step.
- Simple controls evaluated at time 0.
- Reservoirs, and tanks as fixed heads.

**Not yet:** PRV/PSV/FCV/PBV valves and extended-period simulation.

### Topology rules

```csharp
var issues = NetworkTopology.Validate(pipes, tolerance: 0.001, snapDistance: 1.0, sources: plants, allowedEnds: hydrants);
```

The rules are zero-length lines, duplicates, dangles, undershoots/overshoots, crossings without a node, self-intersections, and lines disconnected from every source. Each violation is a row with its location.

### Isolation valves

```csharp
var valves = ValveIsolation.SnapValves(net, valvePoints);          // a valve sits on a pipe, next to one end node
var iso = new ValveIsolation(net, valves);
iso.SegmentsFrame();                                              // valve segments: pipes, valves, length, demand, source
var r = iso.Isolate("P-104");   // valves to close, isolated pipes/nodes, unintended isolation, affected demand
```

## Civil engineering

```csharp
var h = new HorizontalAlignment(pis, radii: [380, 320], spiralLengths: [70, 60], startStation: 0);
h.Curves;                        // Δ, T, L, E, M, LC, TS/SC/CS/ST stations
h.PointAt(1250, offset: 3.5);    // x, y, azimuth
h.StationOffset(x, y);           // inverse
var v = new VerticalAlignment([new Pvi(0, 100), new Pvi(1000, 112, CurveLength: 200), new Pvi(2000, 105)]);
var corridor = new Corridor(h, v, CorridorTemplate.TwoLaneRoad(cutSlope: 2, fillSlope: 3), dem);
corridor.Volumes(interval: 20, cutFactor: 0.9);   // cut/fill areas & volumes (average end area), mass-haul ordinate
corridor.FeatureLines(20); corridor.Footprint(20); corridor.DesignSurface(20);
Earthwork.Volume(dem, corridor.DesignSurface(20));        // grid cut/fill + cut/fill raster
Earthwork.BalancedPadElevation(dem, pad);                  // level pad that balances cut and fill
Subdivision.Split(parcel, parts: 6);                       // equal-area lots, cut perpendicular to the frontage
Subdivision.SplitByArea(parcel, targetArea: 450);
```

**How it's computed:**

- **Clothoids:** integrated numerically, matching SciPy's Fresnel integrals to 1e-9.
- **Spirals:** spiral–curve–spiral layouts close exactly on the forward tangent.
- **Cross-sections:** integrated exactly between the piecewise-linear design and ground profiles.

## BIM: IFC ⇄ GIS

```csharp
var model = IfcModel.Read("building.ifc");     // IFC2x3, IFC4, IFC4x3 (STEP)
model.Storeys; model.Georeference;             // IfcMapConversion / ePSet_MapConversion / site lat-lon → UTM
model.ElementsFrame().ToFile("elements.gpkg"); // footprints in map coordinates, z range, volume, every Pset/Qto as a column
model.BuildingFootprint();                     // LOD1 feature for a city model: footprint, heights, storeys, GFA
IfcWriter.WriteBuildings(buildings, "city.ifc", heightColumn: "height");   // GIS footprints → georeferenced IFC4
```

**Geometry handled:**

- Extrusions of rectangle, circle, ellipse, I-shape, arbitrary and derived profiles, including voids, polylines, indexed curves with arcs and composite curves.
- Faceted B-reps and surface models.
- IFC4 triangulated and polygonal face sets.
- Mapped items (type representations).
- Boolean results: the first operand only, so the result is an upper bound.

Opening subtraction and NURBS are not evaluated.

## CAD: DWG and DGN

```csharp
var dwg = Dwg.Read("site.dwg");                 // R14 (AC1014) … 2018 (AC1032), via ACadSharp (MIT)
Dwg.Write(gdf, "out.dwg", layerColumn: "layer", textColumn: "label");
var dgn = Dgn.Read("survey.dgn");               // MicroStation v7: lines, shapes, complex chains/shapes, arcs, ellipses, text, cells
Dgn.Write(gdf, "out.dgn", levelColumn: "level", colorColumn: "color");
Gpd.ReadFile("plan.dwg"); gdf.ToFile("plan.dgn");   // both are GeoIO drivers
```

**DWG:**

- Block references and hatches are exploded.
- Colours resolve BYLAYER.
- Columns match the DXF reader.

**DGN:**

- Integer coordinates are converted with the TCB's units and global origin.
- DGN v8 is not supported (it's an OLE container).
