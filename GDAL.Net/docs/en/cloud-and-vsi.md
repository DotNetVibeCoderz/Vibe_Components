# Cloud & virtual files

This page covers GDAL's virtual file systems (in-memory files, archives, HTTP and cloud object storage) through `GDAL.Net.Vsi`.

Any GDAL path can use a virtual file system prefix. `Gdal.Open`, `GdalUtils`, drivers and `VirtualFileSystem` all accept them.

| Prefix | Purpose | Helper |
|---|---|---|
| `/vsimem/` | In-memory files | `VirtualFileSystem.NewMemoryPath(".tif")`, `WriteMemoryFile`, `ReadMemoryFile` |
| `/vsizip/`, `/vsitar/`, `/vsigzip/`, `/vsi7z/` | Archives (read; zip can also be written) | `VirtualFileSystem.Zip(archive, inner)`, `Tar(...)`, `Gzip(...)` |
| `/vsicurl/` | HTTP/HTTPS/FTP with range requests | `VirtualFileSystem.Curl(url)` |
| `/vsis3/` | Amazon S3 and S3-compatible storage | `S3(bucket, key)`, `ConfigureS3(...)` |
| `/vsiaz/`, `/vsiadls/` | Azure Blob / Data Lake | `Azure(container, blob)`, `ConfigureAzure(...)` |
| `/vsigs/` | Google Cloud Storage | `GoogleCloud(bucket, key)`, `ConfigureGoogleCloud(...)` |
| `*_streaming/` | Sequential streaming variants | – |

`VirtualFileSystem.Prefixes` lists the prefixes your build supports.

## In-memory files

```csharp
using GDAL.Net;
using GDAL.Net.Vsi;

string path = VirtualFileSystem.NewMemoryPath(".tif");
VirtualFileSystem.WriteMemoryFile(path, File.ReadAllBytes("dem.tif"));   // e.g. bytes from an HTTP upload
using (var ds = Gdal.OpenRaster(path))
{
    Console.WriteLine(ds.RasterWidth);
}
byte[] bytes = VirtualFileSystem.ReadMemoryFile(path, unlink: true);    // copy out and delete
```

## Archives

```csharp
string zip = VirtualFileSystem.NewMemoryPath(".zip");
using (GdalUtils.VectorTranslate(cities, VirtualFileSystem.Zip(zip, "cities.geojson"), ["-f", "GeoJSON"])) { }

// GeoTIFF needs random access: write it elsewhere first, then copy it into the archive
string tif = VirtualFileSystem.NewMemoryPath(".tif");
using (GdalUtils.Translate(dem, tif, ["-of", "GTiff"])) { }
VirtualFileSystem.CopyFile(tif, VirtualFileSystem.Zip(zip, "dem.tif"));

foreach (var entry in VirtualFileSystem.ListDirectory(VirtualFileSystem.Zip(zip)))
    Console.WriteLine(entry);
using var fromZip = Gdal.OpenVector(VirtualFileSystem.Zip(zip, "cities.geojson"));
```

You can read shapefiles straight from a downloaded zip: `Gdal.OpenVector("/vsizip/C:/data/roads.zip/roads.shp")`.

## Files and streams

```csharp
VirtualFileSystem.WriteAllText("/vsimem/notes.txt", "Halo GDAL.Net");
string text = VirtualFileSystem.ReadAllText("/vsimem/notes.txt");
bool exists = VirtualFileSystem.Exists("/vsimem/notes.txt");
VirtualFileSystem.CreateDirectory("/vsimem/out");
VirtualFileSystem.Rename("/vsimem/notes.txt", "/vsimem/out/notes.txt");
VirtualFileSystem.Delete("/vsimem/out/notes.txt");

// System.IO.Stream over any virtual file (works with JsonSerializer, ZipArchive, HttpContent, ...)
using (VsiStream s = VirtualFileSystem.OpenWrite("/vsimem/data.bin")) s.Write([1, 2, 3]);
using (VsiStream s = VirtualFileSystem.OpenRead("/vsimem/data.bin")) Console.WriteLine(s.Length);

// Copy between any two file systems, with optional progress
VirtualFileSystem.CopyFile("/vsicurl/https://example.com/data.tif", "local.tif");
```

## HTTP and cloud-optimized formats

```csharp
Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");   // don't list the remote directory
Gdal.SetConfigOption("CPL_VSIL_CURL_ALLOWED_EXTENSIONS", ".tif,.tiff,.vrt");
Gdal.SetConfigOption("GDAL_HTTP_TIMEOUT", "20");
Gdal.SetConfigOption("VSI_CACHE", "TRUE");

using var remote = Gdal.OpenRaster(VirtualFileSystem.Curl("https://download.osgeo.org/gdal/data/gtiff/small_world.tif"));
float[] preview = remote.GetBand(1).ReadResampled<float>(360, 180);   // only the needed blocks/overviews are fetched
```

COG, FlatGeobuf, GeoParquet and Zarr are designed for this kind of partial access.

## Cloud credentials

```csharp
// Public bucket, no credentials
VirtualFileSystem.ConfigureS3(accessKeyId: null, secretAccessKey: null, region: "us-west-2", noSign: true);
using var s2 = Gdal.OpenRaster(VirtualFileSystem.S3("sentinel-cogs",
    "sentinel-s2-l2a-cogs/48/M/YT/2024/8/S2A_48MYT_20240801_0_L2A/TCI.tif"));

// Per-bucket credentials (path-specific options)
VirtualFileSystem.ConfigureS3(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
    Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"), region: "ap-southeast-3", pathPrefix: "/vsis3/my-private-bucket");
VirtualFileSystem.ConfigureAzure(connectionString: Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING"));
VirtualFileSystem.ConfigureGoogleCloud(applicationCredentialsFile: "service-account.json");

// S3-compatible storage (MinIO, Wasabi, ...)
VirtualFileSystem.ConfigureS3("key", "secret", endpoint: "minio.local:9000");
Gdal.SetConfigOption("AWS_HTTPS", "NO");
Gdal.SetConfigOption("AWS_VIRTUAL_HOSTING", "FALSE");
```

Don't hard-code secrets: read them from environment variables or a secret store. `Gdal.ClearPathSpecificOptions()` removes per-prefix settings, and `VirtualFileSystem.ClearCurlCache()` drops cached remote data.

**Next:** [Multidimensional](multidimensional.md) · [Utilities](utilities.md)
