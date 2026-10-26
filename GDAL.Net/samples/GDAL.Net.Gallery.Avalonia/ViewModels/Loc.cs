using System.ComponentModel;

namespace GDAL.Net.Gallery.Avalonia.ViewModels;

/// <summary>UI chrome strings (EN / ID). Bind with <c>{Binding L[key]}</c>.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly Dictionary<string, Text> s_strings = new()
    {
        ["overview"] = new("Overview", "Ikhtisar"),
        ["components"] = new("Components", "Komponen"),
        ["use_cases"] = new("Use cases", "Use case"),
        ["home_title"] = new("Geospatial data, served raw and live.", "Data geospasial, mentah dan langsung."),
        ["home_lede"] = new(
            "Each card below opens a working example running real GDAL 3.13 through GDAL.Net — on a DEM of the Bandung basin, a Sentinel-2-like scene, Indonesian cities and a climate cube generated on first launch. Change a parameter and the result re-runs; the code tab shows exactly what ran.",
            "Setiap kartu di bawah membuka contoh yang berjalan dengan GDAL 3.13 asli melalui GDAL.Net — pada DEM cekungan Bandung, citra mirip Sentinel-2, kota-kota Indonesia, dan kubus iklim yang dibuat saat pertama kali dijalankan. Ubah parameter dan hasilnya dijalankan ulang; tab kode menampilkan persis apa yang dijalankan."),
        ["parameters"] = new("Parameters", "Parameter"),
        ["run"] = new("Run again", "Jalankan ulang"),
        ["result"] = new("Result", "Hasil"),
        ["code"] = new("Code", "Kode"),
        ["dataset"] = new("Dataset", "Dataset"),
        ["copy"] = new("Copy code", "Salin kode"),
        ["copied"] = new("Copied", "Tersalin"),
        ["running"] = new("Running…", "Menjalankan…"),
        ["preparing"] = new("Generating sample data with GDAL.Net…", "Membuat data sampel dengan GDAL.Net…"),
        ["preparing_hint"] = new("A DEM, a 4-band scene, cities, land cover, DEM tiles and a netCDF cube — done once, cached next to the app.",
            "DEM, citra 4 band, kota, tutupan lahan, tile DEM, dan kubus netCDF — dibuat sekali, disimpan di samping aplikasi."),
        ["output"] = new("Output", "Keluaran"),
        ["table"] = new("Table", "Tabel"),
        ["metrics"] = new("Key figures", "Angka kunci"),
        ["no_map"] = new("This example produces tables and text — see the result panel.", "Contoh ini menghasilkan tabel dan teks — lihat panel hasil."),
        ["error"] = new("The example stopped with an error", "Contoh berhenti karena galat"),
        ["error_hint"] = new("Adjust the parameters and run again.", "Ubah parameter lalu jalankan ulang."),
        ["equivalent"] = new("GDAL equivalent", "Padanan GDAL"),
        ["took"] = new("ran in", "selesai dalam"),
        ["dark"] = new("Dark", "Gelap"),
        ["light"] = new("Light", "Terang"),
        ["clicked"] = new("Clicked feature", "Fitur yang diklik"),
        ["comp_map"] = new("GdalMapView — raster + vector map", "GdalMapView — peta raster + vektor"),
        ["comp_map_desc"] = new("Wheel to zoom at the cursor, drag to pan, double-click to fit, click a city to inspect it. Rasters render off the UI thread for the visible extent only.",
            "Scroll untuk zoom di posisi kursor, seret untuk geser, klik ganda untuk menyesuaikan, klik kota untuk melihat atributnya. Raster dirender di luar UI thread hanya untuk area yang terlihat."),
        ["comp_hist"] = new("GdalHistogramChart & GdalColorLegend", "GdalHistogramChart & GdalColorLegend"),
        ["comp_info"] = new("GdalDatasetInfoPanel", "GdalDatasetInfoPanel"),
        ["comp_srs"] = new("GdalSrsExplorer — search the EPSG database", "GdalSrsExplorer — cari di database EPSG"),
        ["comp_grid"] = new("GdalAttributeGrid", "GdalAttributeGrid"),
        ["comp_ramps"] = new("Built-in color ramps", "Color ramp bawaan"),
        ["network"] = new("Needs internet access", "Butuh akses internet"),
        ["samples_count"] = new("examples", "contoh"),
    };

    public Loc(Lang lang = Lang.En) => Lang = lang;

    public Lang Lang { get; private set; }

    public string this[string key] => s_strings.TryGetValue(key, out var t) ? t[Lang] : key;

    public void Set(Lang lang)
    {
        Lang = lang;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lang)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
