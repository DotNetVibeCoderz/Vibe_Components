# Data multidimensi

Halaman ini membahas kubus netCDF, HDF5, Zarr, dan GRIB lewat API multidimensi: grup, array, dimensi, atribut, serta baca/tulis hyperslab (namespace `GDAL.Net.MultiDimensional`).

## Membuka dan menjelajah

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

## Membaca hyperslab

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

## Melihat array sebagai raster klasik

`AsClassicDataset(xDim, yDim)` memetakan dua dimensi ke x/y dan sisanya menjadi band, sehingga seluruh API raster bisa dipakai (rendering, warping, statistik, gdal_translate):

```csharp
using var classic = temperature.AsClassicDataset(xDimension: 2, yDimension: 1);   // 12 bands, one per month
var stats = classic.GetBand(3).ComputeStatistics();
using var geotiff = GdalUtils.Translate(classic, "march.tif", ["-b", "3"]);
```

## Membuat kubus

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

`Type`/`Direction` dimensi hanya bersifat petunjuk: driver MEM menyimpannya, sedangkan netCDF hanya menyimpan nama dan ukuran.

Dimensi dibuat saat pertama kali dipakai lalu digunakan ulang berdasarkan nama, sehingga variabel koordinat (`lat` di atas) dan variabel data memakai dimensi yang sama. Di netCDF, array yang namanya sama dengan dimensi menjadi variabel koordinat dimensi tersebut.

`MdGroup.CreateGroup(name)` membuat hierarki (netCDF-4, Zarr, MEM). HDF5 hanya bisa dibaca.

**Berikutnya:** [Rendering](rendering.md) · [Cloud & file virtual](cloud-and-vsi.md)
