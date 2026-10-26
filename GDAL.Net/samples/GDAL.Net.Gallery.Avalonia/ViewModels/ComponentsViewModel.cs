using CommunityToolkit.Mvvm.ComponentModel;
using GDAL.Net.Avalonia;
using GDAL.Net.Rendering;
using GDAL.Net.Vector;

namespace GDAL.Net.Gallery.Avalonia.ViewModels;

/// <summary>Live showcase of every GDAL.Net.Avalonia control.</summary>
public sealed partial class ComponentsViewModel : ObservableObject
{
    public ComponentsViewModel(MainViewModel main)
    {
        Main = main;
        _ = LoadAsync();
    }

    public MainViewModel Main { get; }
    public Loc L => Main.L;

    public void RefreshLanguage() => OnPropertyChanged(nameof(L));

    [ObservableProperty] private IReadOnlyList<MapRasterLayer>? _rasters;
    [ObservableProperty] private IReadOnlyList<StyledVector>? _vectors;
    [ObservableProperty] private Histogram? _histogram;
    [ObservableProperty] private double _min;
    [ObservableProperty] private double _max = 1;
    [ObservableProperty] private Dataset? _dem;
    [ObservableProperty] private VectorData? _cities;
    [ObservableProperty] private string? _clicked;

    public ColorRamp Terrain => ColorRamp.Terrain;
    public IReadOnlyList<ColorRamp> Ramps => ColorRamp.All;

    private async Task LoadAsync()
    {
        var data = await Task.Run(() =>
        {
            Main.Context.EnsureData();
            var dem = Main.Context.OpenDem();
            var band = dem.GetBand(1);
            var (min, max) = band.ComputeMinMax();
            var histogram = band.GetHistogram(64);
            using var contours = RasterAlgorithms.GenerateContours(band, 100);
            var contourData = VectorData.Load(contours.GetLayer(0));
            var (vx, vy) = SampleContext.VolcanoUtm;
            var (bx, by) = SampleContext.BandungUtm;
            using var volcano = Geometry.Point(vx, vy);
            using var city = Geometry.Point(bx, by);
            using var ds = Gdal.CreateMemoryVector();
            var layer = ds.CreateLayer("places", null, GeometryType.Point);
            layer.CreateField("name", FieldType.String);
            layer.AddFeature(volcano, new Dictionary<string, object?> { ["name"] = "Tangkuban Perahu" });
            layer.AddFeature(city, new Dictionary<string, object?> { ["name"] = "Bandung" });
            var places = VectorData.Load(layer);
            using var citiesDs = Main.Context.OpenCities();
            var cities = VectorData.Load(citiesDs.GetLayer(0));
            return (dem, min, max, histogram, contourData, places, cities);
        });

        Dem = data.dem;
        Min = data.min;
        Max = data.max;
        Histogram = data.histogram;
        Cities = data.cities;
        Rasters = [new MapRasterLayer(data.dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "DEM")];
        Vectors =
        [
            new StyledVector(data.contourData, VectorStyle.Lines(new Rgba(18, 38, 58, 120), 0.8), "Contours"),
            new StyledVector(data.places, VectorStyle.Points(new Rgba(200, 85, 61), 7) with { LabelField = "name" }, "Places"),
        ];
    }

    public void OnFeatureClicked(MapFeatureEventArgs e)
        => Clicked = $"{e.Layer.Title}: " + string.Join("  ", e.Feature.Attributes.Select(kv => $"{kv.Key} = {GdalAttributeGrid.Format(kv.Value)}"));
}
