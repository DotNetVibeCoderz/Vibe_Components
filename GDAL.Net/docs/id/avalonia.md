# Kontrol Avalonia

`Gravicode.GDAL.Net.Avalonia` menyediakan kontrol desktop untuk Windows, macOS, dan Linux. Kontrol digambar secara native, dan raster dirender di thread latar belakang.

![Galeri Avalonia — komposit warna asli/semu](../images/avalonia-composites.png)

## Instalasi dan konfigurasi

```bash
dotnet add package Gravicode.GDAL.Net.Avalonia
```

```xml
<!-- App.axaml -->
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://GDAL.Net.Avalonia/Themes/GdalNetTheme.axaml" />
</Application.Styles>
```

Tema mengikuti mode terang/gelap dan sudah menyertakan tema Fluent untuk DataGrid. Warnanya bisa ditimpa lewat resource berikut:
- `GdalNet.MapBackground`
- `GdalNet.Ink`
- `GdalNet.InkMuted`
- `GdalNet.Panel`
- `GdalNet.Rule`
- `GdalNet.Accent`

> Avalonia juga punya tipe `RenderOptions`. Tulis milik GDAL lengkap sebagai `GDAL.Net.Rendering.RenderOptions`, atau pakai alias `using GdalRenderOptions = GDAL.Net.Rendering.RenderOptions;`.

## Kontrol

| Kontrol | Fungsi | Anggota utama |
|---|---|---|
| `GdalMapView` | Peta raster + vektor dengan pan/zoom halus | `Rasters` (`MapRasterLayer`), `Vectors` (`StyledVector`), `Extent`, `CrsLabel`, `MapBackground`, `Accent`, `ShowControls`, `ShowScaleBar`, `ShowCoordinates`, `HighlightSelection`, `IsRendering`; event `FeatureClicked`, `ViewChanged`, `PointerCoordinateChanged`; method `ZoomToFit()`, `ZoomBy()`, `ZoomTo(Envelope)` |
| `GdalAttributeGrid` | DataGrid untuk fitur atau tabel | `Data` (`VectorData`) **atau** `Columns` + `Rows`, `MaxRows`, `FeatureSelected`, `Grid` |
| `GdalHistogramChart` | Histogram yang digambar sendiri | `Histogram`, `Ramp`, `RampMin`, `RampMax`, `Title`, `LogScale` |
| `GdalColorLegend` | Legenda ramp | `Ramp`, `Minimum`, `Maximum`, `Unit`, `Title`, `Orientation`, `Ticks` |
| `GdalDatasetInfoPanel` | Inspektor dataset | `Dataset`, `ShowMetadata` |
| `GdalSrsExplorer` | Pencarian EPSG, WKT2/WKT1/PROJJSON/PROJ/ESRI, transformasi lon/lat | kueri, CRS terpilih, font mono |

### GdalMapView

```xml
<Window xmlns:gdal="using:GDAL.Net.Avalonia" ...>
  <gdal:GdalMapView x:Name="Map" CrsLabel="EPSG:32748" />
</Window>
```

```csharp
using GDAL.Net;
using GDAL.Net.Avalonia;
using GDAL.Net.Rendering;
using GdalRenderOptions = GDAL.Net.Rendering.RenderOptions;

var dem = Gdal.OpenRaster("dem.tif");
Map.Rasters = [new MapRasterLayer(dem, new GdalRenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "DEM")];

using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 100);
Map.Vectors = [new StyledVector(VectorData.Load(contours.GetLayer(0)), VectorStyle.Lines(new Rgba(200, 85, 61)), "Kontur")];

Map.FeatureClicked += (_, e) => Title = $"{e.Feature["elev"]} m";
```

Interaksi:
- **Mouse:** scroll wheel untuk zoom di posisi kursor, drag untuk menggeser, klik ganda untuk menampilkan seluruh data.
- **Keyboard:** `+`, `-`, tombol panah, dan `Home` (fit).

Render dilakukan dengan debounce dan bisa dibatalkan. Selama gerakan berlangsung, frame sebelumnya ditransformasikan agar geseran tetap halus.

### Thread safety: `GdalUiSync.Lock`

Dataset GDAL tidak thread-safe, dan peta membacanya di thread latar belakang. Kode Anda yang mengakses atau men-dispose dataset yang sedang tampil di peta harus mengambil lock bersama ini:

```csharp
lock (GdalUiSync.Lock)
{
    Map.Rasters = null;
    dem.Dispose();
}
```

## Keterbatasan

- Hanya raster north-up yang ditampilkan. Raster yang terotasi perlu di-warp dulu.
- Semua peta berbagi satu lock render, sehingga beberapa peta dirender bergantian.
- DataGrid menampilkan sel string, sehingga kolom angka diurutkan sebagai teks.

## Berikutnya

- [Komponen Blazor](blazor.md)
- [Galeri](gallery.md)
- [Rendering](rendering.md)
