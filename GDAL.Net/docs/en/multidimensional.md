# Multidimensional data

This page covers netCDF, HDF5, Zarr and GRIB cubes through the multidimensional API: groups, arrays, dimensions, attributes and hyperslab reads and writes (namespace `GDAL.Net.MultiDimensional`).

## Open and explore

```csharp
using GDAL.Net;
using GDAL.Net.MultiDimensional;

using var ds = Gdal.OpenMultidimensional("climate_indonesia.nc");
using var root = ds.RootGroup!;
Console.WriteLine(string.Join(", ", root.ArrayNames));      // time, lat, lon, temperature, rainfall
Console.WriteLine(string.Join(", ", root.GroupNames));
foreach (var a in root.Attributes) Console.WriteLine($"{a.Name} = {a.StringValue ?? a.NumericValue.ToString()}");

using var temperature = root.OpenArray("temperature");
Console.WriteLine(temperature);                              // Array '/temperature' time=12×lat=70×lon=190 Float32
foreach (MdDimension d in temperature.Dimensions)
    Console.WriteLine($"{d.Name}: {d.Size} ({d.Type}, {d.Direction}, indexed by {d.IndexingVariable})");
Console.WriteLine($"{temperature.Unit}, nodata {temperature.NoDataValue}, scale {temperature.Scale}, offset {temperature.Offset}");

string json = GdalUtils.MultiDimInfo(ds);                    // gdalmdiminfo
```

## Read hyperslabs

```csharp
// One month: start [2, 0, 0], count [1, 70, 190] → 13 300 values in C order
var march = new float[70 * 190];
temperature.Read<float>(start: [2, 0, 0], count: [1, 70, 190], march);

// A time series at one grid cell
var series = new float[12];
temperature.Read<float>([0, 35, 95], [12, 1, 1], series);

// Everything
float[] all = temperature.ReadAll<float>();
double[] lats = root.OpenArray("lat").ReadAll<double>();
```

## View an array as a classic raster

`AsClassicDataset(xDim, yDim)` maps two dimensions to x/y and the remaining ones to bands, so you can use the whole raster API (rendering, warping, statistics, gdal_translate):

```csharp
using var classic = temperature.AsClassicDataset(xDimension: 2, yDimension: 1);   // 12 bands, one per month
var stats = classic.GetBand(3).ComputeStatistics();
using var geotiff = GdalUtils.Translate(classic, "march.tif", ["-b", "3"]);
```

## Create cubes

```csharp
using var nc = Gdal.GetDriver("netCDF").CreateMultidimensional("cube.nc");   // or "MEM", "Zarr"
using var g = nc.RootGroup!;
g.SetAttribute("title", "My cube");

(string, ulong, string?, string?)[] dims =
[
    ("time", 3, "TEMPORAL", null),
    ("lat", 4, "HORIZONTAL_Y", "NORTH"),
    ("lon", 5, "HORIZONTAL_X", "EAST"),
];
using (var lat = g.CreateArray("lat", DataType.Float64, [dims[1]])) lat.WriteAll<double>([-6.0, -6.5, -7.0, -7.5]);
using var t = g.CreateArray("temperature", DataType.Float32, dims);
t.NoDataValue = -999;                    // netCDF: set the fill value before writing data
t.Unit = "degC";
t.SetAttribute("long_name", "Air temperature");
t.WriteAll<float>(Enumerable.Range(0, 60).Select(i => 20f + i * 0.1f).ToArray());
t.Write<float>(start: [0, 0, 0], count: [1, 1, 2], [25f, 26f]);
```

Dimension `Type`/`Direction` are hints: the MEM driver keeps them, while netCDF stores only names and sizes.

Dimensions are created on first use and reused by name, so coordinate variables (`lat` above) and data variables share them. Arrays named after a dimension become its coordinate variable in netCDF.

`MdGroup.CreateGroup(name)` builds hierarchies (netCDF-4, Zarr, MEM). HDF5 is read-only.

**Next:** [Rendering](rendering.md) · [Cloud & virtual files](cloud-and-vsi.md)
