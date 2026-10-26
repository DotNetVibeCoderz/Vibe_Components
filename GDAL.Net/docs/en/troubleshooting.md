# Troubleshooting

This page lists common problems with GDAL.Net and how to fix them.

## `DllNotFoundException` / `TypeInitializationException` on the first call

The native GDAL library could not be loaded.

- Make sure the app references `Gravicode.GDAL.Net`, which brings the `MaxRev.Gdal.*Runtime.Minimal` packages. Check that `runtimes/<rid>/native/gdal.dll` (Windows), `libgdal.so.*` (Linux) or `libgdal.*.dylib` (macOS) exists in the output.
- Only 64-bit processes are supported (x64/arm64). Turn off "Prefer 32-bit".
- On Windows, all DLLs of the runtime folder must stay together. Don't copy `gdal.dll` alone.
- To use a system GDAL instead, set `GDALNET_LIBRARY_PATH` to the full path of the library.
- Linux containers need the usual C/C++ runtime (`libstdc++`). Alpine/musl is not covered by the bundled build.
- `Gdal.NativeLibraryPath` shows which library was loaded.

## "PROJ: proj_create_from_database: Cannot find proj.db"

`Gdal.ProjDataPath` is `null`. The runtime packages ship `proj.db` in `runtimes/<rid>/native/maxrev.gdal.core.libshared/`. If you publish with a custom layout, copy that folder next to your app, or set `PROJ_DATA` to a folder that contains `proj.db` before the first GDAL call.

## Missing `gdal-data`

Drivers such as DXF, GML, S-57 and netCDF CF tables read support files from `GDAL_DATA`. The package copies `gdal-data/` into your output (via `buildTransitive`). Check `Gdal.DataPath`, or set the `GDAL_DATA` environment variable.

## Coordinates come out swapped

GDAL.Net uses x = longitude, y = latitude by default. If you set `AxisMappingStrategy.AuthorityCompliant`, EPSG:4326 expects latitude first. WKT taken from other tools does not change this: GDAL.Net applies the GIS order to every `SpatialReference` it creates.

## `/vsicurl/` and cloud access

- Use `Gdal.Log += …` to see HTTP errors (403, 404, timeouts).
- Set `GDAL_DISABLE_READDIR_ON_OPEN=EMPTY_DIR` to avoid slow directory listings on open.
- Behind a proxy, set `GDAL_HTTP_PROXY` (and `GDAL_HTTP_PROXYUSERPWD`).
- On corporate networks with TLS inspection, set `CURL_CA_BUNDLE` to your CA bundle, or, for testing only, `GDAL_HTTP_UNSAFESSL=YES`.
- For public S3 buckets, call `VirtualFileSystem.ConfigureS3(null, null, region, noSign: true)`.
- After remote files change, call `VirtualFileSystem.ClearCurlCache()`.

## "Cannot create TIFF file due to missing codec" / unsupported options

The bundled build is the *minimal* MaxRev build. Some codecs are not included, for example LERC. Check `Gdal.BuildInfo` and the driver's `CreationOptionList`. Other codec rules:

- WebP and JPEG in GeoTIFF/COG need Byte data. WebP also needs 3 or 4 bands.
- For a 1-band Float32 DEM, use `DEFLATE`, `ZSTD` or `LZW` with `PREDICTOR=YES`.

## GeoTIFF inside a ZIP fails to write

`/vsizip/` writes are sequential, and GeoTIFF needs random access. Write to `/vsimem/` first, then `VirtualFileSystem.CopyFile(tmp, VirtualFileSystem.Zip(zip, "file.tif"))`.

## Output file is empty or truncated

Several drivers (GeoJSON, KML, Shapefile, PNG) finish writing when the dataset is closed. Dispose the dataset returned by `Create`, `CreateCopy`, `Translate` or `VectorTranslate` before reading the file, or call `Close()` to surface flush errors.

## `ObjectDisposedException`

You used a band, layer, feature or geometry after its owner (the dataset or feature) was disposed. Keep the owner alive, or `Clone()` geometries you need later.

## Crashes or corrupted results under parallel load

A `Dataset` (and everything obtained from it) must only be used by one thread at a time. Open the file once per thread.

## Gallery samples or `GdalCli` fail with "invalid arguments"

The `gdal` CLI argument names follow the GDAL version you run (3.13 uses `--output-crs`, `--output-format`, …). Ask GDAL with `GdalCli.GetUsageJson("raster", "reproject")`.

## Windows PowerShell 5.1 notes

- `cargo` and `dotnet` write progress to stderr. With `$ErrorActionPreference = 'Stop'` that becomes an error, so `build/build-native.ps1` wraps native commands accordingly.
- `dotnet test` on .NET 10 uses Microsoft.Testing.Platform (see `global.json`). Run `dotnet test --project tests/GDAL.Net.Tests`.

## Getting more diagnostics

```csharp
Gdal.SetConfigOption("CPL_DEBUG", "ON");
Gdal.Log += m => Console.WriteLine($"[{m.Level}] {m.Message}");
```

**Back to:** [Index](index.md)
