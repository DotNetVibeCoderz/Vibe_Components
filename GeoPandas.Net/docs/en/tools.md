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
| `gpd sample <districts\|roads\|pois\|water\|cities\|siteplan\|dem\|multispectral> <out>` | Export sample data |
| `gpd ask <file> "question" [--out f]` | GeoLLM question (needs the `GEOPANDAS_LLM_*` environment variables) |
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
