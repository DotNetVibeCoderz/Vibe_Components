# Tools: CLI, gallery, templates, benchmarks

## `gpd` command-line tool

```bash
dotnet tool install -g GeoPandas.Net.Cli        # or: dotnet run --project tools/GeoPandas.Net.Cli -- <command>
```

| Command | Purpose |
|---|---|
| `gpd info <file> [--layer L]` | Schema, CRS, bounds, geometry types |
| `gpd head <file> [--n 10]` | First rows |
| `gpd layers <file.gpkg>` | GeoPackage layers |
| `gpd convert <in> <out> [--to-crs EPSG:x] [--layer L] [--out-layer L]` | Format conversion and reprojection |
| `gpd buffer <in> <out> --distance d [--to-crs …]` | Buffer |
| `gpd dissolve <in> <out> [--by col] [--agg sum\|mean\|first]` | Dissolve |
| `gpd sjoin <left> <right> <out> [--predicate p] [--how inner\|left\|right]` | Spatial join (right is reprojected to match left) |
| `gpd clip <in> <mask> <out>` | Clip |
| `gpd plot <in> <out.svg> [--column c] [--cmap name] [--scheme quantiles\|equal\|jenks\|continuous] [--label col] [--title t]` | SVG map |
| `gpd crs <definition>` | Show PROJ and WKT for EPSG / PROJ / WKT input |
| `gpd transform <from> <to> <x> <y>` | Transform one coordinate |
| `gpd route <lines> --from x,y --to x,y [--cost col] [--oneway col] [--out f]` | Shortest path (A*) |
| `gpd raster info\|slope\|aspect\|hillshade\|fill\|flowacc\|contours\|ndvi\|plot …` | Raster tools |
| `gpd scan <file> [--bbox x0,y0,x1,y1] [--where "c>=v and d=x"] [--select a,b] [--limit n] [--to-crs C] [--explain] [--out f]` | Lazy query with pushdown (FlatGeobuf, GeoParquet, GeoPackage) |
| `gpd las info\|dsm\|dtm\|ndsm\|convert <file> … [--cell 1] [--class 2,6] [--step n] [--format 6]` | LAS/LAZ point clouds |
| `gpd netcdf info <file.nc>` / `gpd netcdf raster <file.nc> <var> <out.tif> [--index t,level]` | NetCDF summary and grid export |
| `gpd repo init\|commit\|log\|diff\|checkout\|tag <dir> …` | Dataset versioning (`--key`, `--m`, `--out changes.geojson`) |
| `gpd crs search <text>` / `gpd crs at <lon> <lat>` | Search the EPSG dataset by name or by area of use |
| `gpd regress <file> <y> <x1,x2> [--model ols\|lag\|error\|gwr] [--weights queen\|rook\|knn:6] [--out f]` | Spatial regression and GWR |
| `gpd water info\|solve <network.inp> [--close P1,P2] [--out-nodes f] [--out-links f]` | EPANET-compatible hydraulics |
| `gpd topology <lines> [--tolerance 0.001] [--snap 1] [--sources pts] [--out f]` | Network topology rules |
| `gpd ifc info\|elements\|footprint <model.ifc> [--out f]` / `gpd ifc from <buildings> <out.ifc> [--height h]` | IFC ⇄ GIS |
| `gpd transform <from> <to> <x> <y> [--z h]` | 3-D transform, honouring `+nadgrids` and `+geoidgrids` |
| `gpd geoid <lon> <lat> [--model f.gtx\|.isg\|.tif] [--h h]` | Geoid undulation and orthometric height |
| `gpd survey traverse <legs.csv> --start E,N --backsight AZ [--close-angle A\|--close-on E,N]` | Bowditch / Transit traverse |
| `gpd survey adjust <network.txt> [--out ellipses.geojson]` | Least-squares 2-D and leveling adjustment |
| `gpd gnss spp <obs> <nav> [--out track.geojson] [--mask 10]` | RINEX single point positioning |
| `gpd copc info\|read <file.copc.laz> … [--bbox …] [--depth n]` | COPC hierarchy and spatial queries |
| `gpd s2\|landsat info\|ndvi <product> [out.tif]` / `gpd stac info\|get <item.json> …` | Satellite products |
| `gpd sample <districts\|roads\|pois\|water\|cities\|siteplan\|dem\|multispectral\|lidar\|climate> <out>` | Export sample data |
| `gpd ask <file> "question" [--out f]` | GeoLLM question (needs the `GEOPANDAS_LLM_*` environment variables) |
| `gpd h3 bin <points> <res> [--value col] [--out f]` / `gpd h3 cell <lat> <lon> <res>` / `gpd h3 disk <cell> <k>` / `gpd h3 info <cell>` | H3 hexagon binning and cell lookups |
| `gpd s2cell <lat> <lon> [--level 30]` | S2 cell ids, tokens and areas from a level down to level 5 |
| `gpd partition <file> <out_dir> [--scheme kd\|grid\|h3\|column] [--size n] [--column c]` | Write a partitioned GeoParquet dataset |
| `gpd dataset info <dir>` / `gpd dataset read <dir> [--bbox x0,y0,x1,y1] [--out f]` | Inspect a partitioned dataset or read it with partition pruning |
| `gpd tiles3d buildings <file> <out_dir> [--height h] [--max-features 500]` / `gpd tiles3d terrain <dem.tif> <out_dir> [--chunk 128]` | OGC 3D Tiles 1.1 tilesets |
| `gpd explore <file> [--column c] [--out map.html] [--basemap osm\|none]` | Interactive MapLibre map (opens in the browser without `--out`) |
| `gpd geocode "<text>" [--provider azure\|aws] [--index name] [--top 5] [--out f]` | Geocoding with Azure Maps (`AZURE_MAPS_KEY`) or AWS Location (`AWS_REGION` plus SigV4 credentials or `AWS_LOCATION_API_KEY`) |
| `gpd about` | Version, kernel backend, drivers |

## Gallery apps

- **Blazor** (`gallery/GeoPandas.Net.Gallery.Blazor`): an Interactive Server app at `http://localhost:5280`, with deep links such as `/sample/route?lang=id`.
- **Avalonia** (`gallery/GeoPandas.Net.Gallery.Avalonia`): a desktop app that draws the same display list natively. Run it with `--sample <id> --lang id --screenshot out.png` to export a screenshot without opening a window.

Both render from `GeoPandas.Net.Gallery.Shared/GalleryCatalog.cs`. To add a sample, append a `GallerySample` there with bilingual title/summary, a code snippet and a `Build` function.

Design system: map paper `#EDF1EA`, slate ink `#1B2A33`, contour brown `#9A6B3F`, water blue `#2F6F9F` and UTM-grid magenta `#B0306A`. Type is Bricolage Grotesque (display), Public Sans (body) and JetBrains Mono (data/code). The signature element is the map-sheet frame with coordinate ticks (`MapScene.ShowFrame`).

## Templates

```bash
dotnet new install GeoPandas.Net.Templates      # or: dotnet new install templates/GeoPandas.Net.Templates/content/gpd-console
dotnet new gpd-console  -n Analysis
dotnet new gpd-blazor   -n WebMap
dotnet new gpd-avalonia -n DesktopMap
```

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/GeoPandas.Net.Benchmarks -- --filter "*"
```

The suites are `SpatialJoinBench` (indexed sjoin vs a nested loop), `CrsBench` (1 M points to UTM and to BNG with a datum shift), `KernelBench` (Rust vs SIMD haversine and bbox filter) and `RoutingBench` (Dijkstra vs A* vs Contraction Hierarchies). Results from the development machine are recorded in [Progress.md](../../Progress.md).
