# Analisis

## Statistik spasial

```csharp
var w  = SpatialWeights.Queen(kecamatan);              // juga Rook, KNearest(k), DistanceBand(d)
var mi = Autocorrelation.MoransI(kecamatan["income_mio_idr"].ToDoubleArray(), w.RowStandardize());
Console.WriteLine(mi);                                 // nilai I, z-score, p (normal & permutasi)

var lisa = Autocorrelation.LocalMoran(nilai, w);       // kuadran HH/LL/HL/LH
var hot  = Autocorrelation.HotSpots(kecamatan, "income_mio_idr", w);   // kolom gi_z, gi_p, hotspot

var klaster = Clustering.Dbscan(poi, eps: 300, minPoints: 4);          // kolom "cluster", -1 = noise
var km      = Clustering.KMeans(poi, k: 6);
var vg      = Variogram.Fit(xs, ys, zs, VariogramModel.Spherical);
var (z, varians) = Interpolation.OrdinaryKriging(xs, ys, zs, gridX, gridY, vg);
```

## Jaringan & routing

```csharp
var graf = NetworkGraph.FromLines(jalan, costColumn: "travel_time_s", onewayColumn: "oneway");
var rute = graf.ShortestPath(Geo.Point(x0, y0), Geo.Point(x1, y1), PathAlgorithm.AStar);
Console.WriteLine($"{rute.Cost / 60:F1} menit, {rute.Length / 1000:F2} km");

var ch = graf.BuildContractionHierarchy();             // untuk ribuan kueri cepat
var (ruas, isokron) = graf.ServiceArea(graf.NearestNode(hx, hy), maxCost: 600, bufferDistance: 150);

// Jaringan utilitas (air/gas/listrik/telekomunikasi): telusuri hanya melalui katup yang terbuka
var pipa = NetworkGraph.FromLines(jaringanPipa, directed: true);
var terlayani = pipa.Trace(instalasi, e => (string?)jaringanPipa["valve"].GetValue(e.SourceRow) == "open", downstreamOnly: true);
```

## Raster, terrain & hidrologi

```csharp
var dem = GeoTiff.Read("dem.tif");                     // GeoTIFF/BigTIFF/COG; Deflate, LZW
var lereng   = Terrain.Slope(dem);
var bayangan = Terrain.Hillshade(dem, azimuth: 315, altitude: 45);
var terlihat = Terrain.Viewshed(dem, menaraX, menaraY, observerHeight: 40);
var kontur   = dem.Contours(interval: 25);

var arah  = Hydrology.FlowDirection(Hydrology.FillSinks(dem));
var akum  = Hydrology.FlowAccumulation(arah);
var das   = Hydrology.Watershed(arah, outletX, outletY);
var sungai = Hydrology.Streams(akum, threshold: 400);
```

## Penginderaan jauh

```csharp
var citra = GeoTiff.Read("sentinel2.tif");
var ndvi  = SpectralIndex.Ndvi(SpectralIndex.Band(citra, 7), SpectralIndex.Band(citra, 3));   // B8 (NIR), B4 (red)
var (selisih, perubahan) = SpectralIndex.ChangeDetection(ndviAwal, ndviAkhir);              // ambang Otsu
var statistik = dem.ZonalStatistics(kecamatan);                                             // statistik zonal per poligon
```
