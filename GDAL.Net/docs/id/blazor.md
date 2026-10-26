# Komponen Blazor

`Gravicode.GDAL.Net.Blazor` menyediakan komponen Razor untuk menampilkan data GDAL di Blazor Web App.

GDAL adalah kode native, jadi komponen ini berjalan dengan render mode **Interactive Server**. Raster dirender di server lewat `RasterRenderer` lalu dikirim sebagai gambar PNG. Vektor digambar sebagai SVG di browser.

![Galeri Blazor — kontur di atas DEM berbayang](../images/blazor-contours.png)

## Instalasi dan konfigurasi

```bash
dotnet add package Gravicode.GDAL.Net.Blazor
```

```razor
@* _Imports.razor *@
@using GDAL.Net
@using GDAL.Net.Rendering
@using GDAL.Net.Blazor
```

```csharp
// Program.cs
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);   // frame PNG untuk peta besar
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
```

Asset statis (modul JS peta) disajikan dari `_content/Gravicode.GDAL.Net.Blazor/` dan dimuat otomatis.

Tema komponen diatur lewat variabel CSS (`--gdal-*`) yang bisa ditimpa di stylesheet Anda.

## Komponen

| Komponen | Fungsi | Parameter utama |
|---|---|---|
| `GdalMap` | Peta pan/zoom untuk raster dan vektor | `Rasters` (`MapRaster(Dataset, RenderOptions, Title)`), `Vectors` (`StyledVector`), `Extent`, `ViewKey`, `CrsLabel`, `Geographic`, `Overscan`, `ShowControls`, `OnFeatureClick`, `OnClick`, `OnRendered`, `Class`, `Style` |
| `GdalAttributeTable` | Tabel yang bisa diurutkan dan berhalaman | `Data` (`VectorData`) **atau** `Columns` + `Rows`, `PageSize`, `Caption`, `EmptyText` |
| `GdalHistogram` | Histogram SVG | `Histogram`, `Ramp`, `Title`, `Unit`, `Log`, `Width`, `Height` |
| `GdalColorLegend` | Gradien ramp dengan label | `Ramp`, `Min`, `Max`, `Unit`, `Title`, `ShowMidpoint` |
| `GdalDatasetInfo` | Driver, ukuran, band, CRS, geotransform, metadata | `Dataset`, `ShowMetadata` |
| `GdalSrsExplorer` | Pencarian EPSG, WKT2/PROJJSON/PROJ/ESRI, transformasi titik | `InitialCrs`, `Authority`, `MaxResults`, `Lon`, `Lat`, `OnSelected` |
| `GdalProgressBar` | Progres operasi panjang | `Value` (0..1), `Indeterminate`, `Label` |

### GdalMap

```razor
@page "/terrain"
@rendermode InteractiveServer
@implements IDisposable

<GdalMap Rasters="rasters" Vectors="vectors" CrsLabel="EPSG:32748" Style="height:600px"
         OnFeatureClick="OnClick" />

<p>@status</p>

@code {
    Dataset? dem;
    IReadOnlyList<MapRaster> rasters = [];
    List<StyledVector> vectors = [];
    string status = "";

    protected override void OnInitialized()
    {
        dem = Gdal.OpenRaster("dem.tif");
        rasters = [new MapRaster(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "DEM")];
        using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 100);
        vectors = [new StyledVector(VectorData.Load(contours.GetLayer(0)), VectorStyle.Lines(new Rgba(200, 85, 61)), "Contours")];
    }

    void OnClick(MapFeatureClick click) => status = $"Kontur {click.Feature["elev"]} m di {click.X:0}, {click.Y:0}";

    public void Dispose() => dem?.Dispose();
}
```

Perilaku peta:
- **Interaksi.** Scroll wheel dan drag bergerak langsung di sisi klien. Setelah gerakan berhenti, server merender area yang terlihat (ditambah `Overscan`) sesuai ukuran piksel viewport. Overview membuatnya tetap cepat di semua level zoom.
- **Reset tampilan.** Mengubah `ViewKey` mengembalikan tampilan ke `Extent` (atau ke batas semua layer).
- **Gaya vektor.** `VectorStyle` mendukung warna dan ukuran titik per fitur (`FillSelector`, `RadiusSelector`) serta `LabelField`. Label yang saling tumpang tindih disembunyikan.
- **Thread safety.** Dataset yang dipakai peta tidak boleh diakses bersamaan oleh kode lain.

### Tabel atribut, histogram, dan legenda

```razor
<GdalAttributeTable Data="cities" PageSize="15" Caption="Kota" />
<GdalHistogram Histogram="@band.GetHistogram(64)" Ramp="ColorRamp.Terrain" Unit="m" />
<GdalColorLegend Ramp="ColorRamp.Ndvi" Min="-1" Max="1" Title="NDVI" />
```

### Penjelajah CRS

```razor
<GdalSrsExplorer InitialCrs="EPSG:32748" Lon="107.6191" Lat="-6.9175" OnSelected="crs => chosen = crs" />
```

## Keterbatasan

- WebAssembly tidak didukung, karena library GDAL native tidak bisa berjalan di browser.
- Viewport besar memakan bandwidth, karena setiap render ulang mengirim frame PNG lewat SignalR.
- Puluhan ribu fitur vektor akan lambat jika digambar sebagai satu SVG. Sederhanakan dulu (`VectorData.Load(layer, simplifyTolerance: …)`) atau saring fiturnya.

## Berikutnya

- [Kontrol Avalonia](avalonia.md)
- [Galeri](gallery.md)
- [Rendering](rendering.md)
