using CesiumNet.Interop;

namespace CesiumNet;

/// <summary>Imagery layer management. Layer 0 is the base layer.</summary>
public sealed class ImageryApi
{
    private readonly CesiumController _c;
    internal ImageryApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Adds a layer on top of the stack and returns its id.</summary>
    public ValueTask<string> AddAsync(ImageryLayerOptions layer, CancellationToken cancellationToken = default)
        => _c.CallForIdAsync("imagery.add", JsArgs.Rent().Obj(layer, J.ImageryLayerOptions).ToJson(), cancellationToken);

    /// <summary>Replaces the base (bottom) layer and returns its id.</summary>
    public ValueTask<string> SetBaseLayerAsync(ImageryLayerOptions layer, CancellationToken cancellationToken = default)
        => _c.CallForIdAsync("imagery.setBase", JsArgs.Rent().Obj(layer, J.ImageryLayerOptions).ToJson(), cancellationToken);

    /// <summary>Updates opacity, color adjustments, visibility or split side.</summary>
    public ValueTask UpdateAsync(string id, ImageryLayerUpdate update)
        => _c.CallVoidAsync("imagery.update", JsArgs.Rent().Str(id).Obj(update, J.ImageryLayerUpdate).ToJson());

    /// <summary>Sets layer opacity (0..1).</summary>
    public ValueTask SetAlphaAsync(string id, double alpha) => UpdateAsync(id, new ImageryLayerUpdate { Alpha = alpha });

    public ValueTask RemoveAsync(string id) => _c.CallVoidAsync("imagery.remove", JsArgs.Rent().Str(id).ToJson());
    public ValueTask RaiseAsync(string id) => _c.CallVoidAsync("imagery.order", JsArgs.Rent().Str(id).Str("raise").ToJson());
    public ValueTask LowerAsync(string id) => _c.CallVoidAsync("imagery.order", JsArgs.Rent().Str(id).Str("lower").ToJson());
    public ValueTask RaiseToTopAsync(string id) => _c.CallVoidAsync("imagery.order", JsArgs.Rent().Str(id).Str("top").ToJson());
    public ValueTask LowerToBottomAsync(string id) => _c.CallVoidAsync("imagery.order", JsArgs.Rent().Str(id).Str("bottom").ToJson());

    /// <summary>Sets the swipe/split position (0..1) used by layers with a <see cref="SplitDirection"/>.</summary>
    public ValueTask SetSplitPositionAsync(double position)
        => _c.Scene.ConfigureAsync(new SceneSettings { SplitPosition = position });

    /// <summary>Layer ids from bottom to top.</summary>
    public async ValueTask<string[]> GetIdsAsync()
        => await _c.CallAsync("imagery.ids", JsArgs.Empty, J.StringArray).ConfigureAwait(false) ?? [];
}

/// <summary>Terrain and elevation.</summary>
public sealed class TerrainApi
{
    private readonly CesiumController _c;
    internal TerrainApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Switches the terrain provider.</summary>
    public ValueTask SetProviderAsync(TerrainOptions terrain, CancellationToken cancellationToken = default)
        => _c.CallVoidAsync("terrain.set", JsArgs.Rent().Obj(terrain, J.TerrainOptions).ToJson(), cancellationToken);

    /// <summary>Exaggerates terrain heights (1 = real).</summary>
    public ValueTask SetExaggerationAsync(double factor, double relativeHeight = 0)
        => _c.Scene.ConfigureAsync(new SceneSettings { VerticalExaggeration = factor, VerticalExaggerationRelativeHeight = relativeHeight });

    /// <summary>
    /// Samples terrain heights at the given points (most detailed level available).
    /// Returned points carry the terrain height in <see cref="GeoPoint.Height"/>.
    /// </summary>
    public async ValueTask<GeoPoint[]> SampleHeightsAsync(IReadOnlyList<GeoPoint> points, CancellationToken cancellationToken = default)
        => await _c.CallAsync("terrain.sample", JsArgs.Rent().Obj(points, J.IReadOnlyListGeoPoint).ToJson(), J.GeoPointArray, cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Height of the terrain at one point (meters), or null if unavailable.</summary>
    public async ValueTask<double?> GetHeightAsync(GeoPoint point, CancellationToken cancellationToken = default)
    {
        var r = await SampleHeightsAsync([point], cancellationToken).ConfigureAwait(false);
        return r.Length > 0 && !double.IsNaN(r[0].Height) ? r[0].Height : null;
    }

    /// <summary>Builds an elevation profile along the geodesic from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public async ValueTask<ProfileSample[]> GetProfileAsync(GeoPoint start, GeoPoint end, int samples = 100, CancellationToken cancellationToken = default)
    {
        var line = Geodesy.Interpolate(start.WithHeight(0), end.WithHeight(0), samples);
        var heights = await SampleHeightsAsync(line, cancellationToken).ConfigureAwait(false);
        var total = Geodesy.Distance(start, end);
        var result = new ProfileSample[heights.Length];
        for (var i = 0; i < heights.Length; i++)
            result[i] = new ProfileSample(total * i / (heights.Length - 1), heights[i]);
        return result;
    }

    /// <summary>Hides the globe inside (or, with <paramref name="inverse"/>, outside) polygons — e.g. to reveal underground assets.</summary>
    public ValueTask SetClippingPolygonsAsync(IReadOnlyList<IReadOnlyList<GeoPoint>>? polygons, bool inverse = false)
        => _c.CallVoidAsync("terrain.clip", JsArgs.Rent().Obj(polygons, J.IReadOnlyListIReadOnlyListGeoPoint).Bool(inverse).ToJson());
}

/// <summary>OGC 3D Tiles.</summary>
public sealed class TilesetsApi
{
    private readonly CesiumController _c;
    internal TilesetsApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Loads a tileset and returns its id.</summary>
    public ValueTask<string> AddAsync(TilesetOptions tileset, CancellationToken cancellationToken = default)
        => _c.CallForIdAsync("tilesets.add", JsArgs.Rent().Obj(tileset, J.TilesetOptions).ToJson(), cancellationToken);

    /// <summary>Applies a declarative style (null resets).</summary>
    public ValueTask SetStyleAsync(string id, TilesetStyle? style)
        => _c.CallVoidAsync("tilesets.style", JsArgs.Rent().Str(id).Obj(style, J.TilesetStyle).ToJson());

    /// <summary>Updates show/quality/clipping/shader options.</summary>
    public ValueTask UpdateAsync(string id, TilesetOptions changes)
        => _c.CallVoidAsync("tilesets.update", JsArgs.Rent().Str(id).Obj(changes, J.TilesetOptions).ToJson());

    /// <summary>Sets a float uniform on the tileset's custom shader.</summary>
    public ValueTask SetShaderUniformAsync(string id, string name, double value)
        => _c.CallVoidAsync("tilesets.uniform", JsArgs.Rent().Str(id).Str(name).Num(value).ToJson());

    public ValueTask RemoveAsync(string id) => _c.CallVoidAsync("tilesets.remove", JsArgs.Rent().Str(id).ToJson());
}

/// <summary>Vector data sources.</summary>
public sealed class DataSourcesApi
{
    private readonly CesiumController _c;
    internal DataSourcesApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Loads a data source and returns its id.</summary>
    public ValueTask<string> LoadAsync(DataSourceOptions source, CancellationToken cancellationToken = default)
        => _c.CallForIdAsync("dataSources.load", JsArgs.Rent().Obj(source, J.DataSourceOptions).ToJson(), cancellationToken);

    public ValueTask SetVisibleAsync(string id, bool visible)
        => _c.CallVoidAsync("dataSources.show", JsArgs.Rent().Str(id).Bool(visible).ToJson());

    public ValueTask RemoveAsync(string id) => _c.CallVoidAsync("dataSources.remove", JsArgs.Rent().Str(id).ToJson());

    /// <summary>Number of entities in a data source.</summary>
    public ValueTask<int> CountAsync(string id) => _c.CallAsync("dataSources.count", JsArgs.Rent().Str(id).ToJson(), J.Int32);
}

/// <summary>High-volume primitives.</summary>
public sealed class PrimitivesApi
{
    private readonly CesiumController _c;
    internal PrimitivesApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Adds a GPU point collection (handles 100k+ points smoothly). Returns its id.</summary>
    public ValueTask<string> AddPointsAsync(PointCollectionOptions points, CancellationToken cancellationToken = default)
        => _c.CallForIdAsync("primitives.addPoints", JsArgs.Rent().Obj(points, J.PointCollectionOptions).ToJson(), cancellationToken);

    public ValueTask RemoveAsync(string id) => _c.CallVoidAsync("primitives.remove", JsArgs.Rent().Str(id).ToJson());

    public ValueTask SetVisibleAsync(string id, bool visible)
        => _c.CallVoidAsync("primitives.show", JsArgs.Rent().Str(id).Bool(visible).ToJson());
}

/// <summary>Simulation clock.</summary>
public sealed class ClockApi
{
    private readonly CesiumController _c;
    internal ClockApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    public ValueTask ConfigureAsync(ClockOptions options)
        => _c.CallVoidAsync("clock.configure", JsArgs.Rent().Obj(options, J.ClockOptions).ToJson());

    public ValueTask PlayAsync() => ConfigureAsync(new ClockOptions { ShouldAnimate = true });
    public ValueTask PauseAsync() => ConfigureAsync(new ClockOptions { ShouldAnimate = false });
    public ValueTask SetMultiplierAsync(double multiplier) => ConfigureAsync(new ClockOptions { Multiplier = multiplier });
    public ValueTask SetTimeAsync(DateTimeOffset time) => ConfigureAsync(new ClockOptions { CurrentTime = time });

    public async ValueTask<ClockState> GetStateAsync()
        => await _c.CallAsync("clock.getState", JsArgs.Empty, J.ClockState).ConfigureAwait(false)
           ?? throw new CesiumInteropException("clock.getState", "No state returned.");
}

/// <summary>Scene-wide rendering settings and effects.</summary>
public sealed class SceneApi
{
    private readonly CesiumController _c;
    internal SceneApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>Morphs between 3D, 2D and Columbus view. Completes when the morph has finished.</summary>
    public ValueTask SetModeAsync(SceneMode mode, double duration = 1.5)
        => _c.CallVoidAsync("scene.mode", JsArgs.Rent().Str(mode.ToString()).Num(duration).ToJson());

    /// <summary>Applies lighting, shadows, atmosphere, fog, post-processing… (null properties are left unchanged).</summary>
    public ValueTask ConfigureAsync(SceneSettings settings)
        => _c.CallVoidAsync("scene.configure", JsArgs.Rent().Obj(settings, J.SceneSettings).ToJson());

    /// <summary>Enables a screen-space weather effect (rain, snow, fog) or turns it off.</summary>
    public ValueTask SetWeatherAsync(WeatherOptions weather)
        => _c.CallVoidAsync("scene.weather", JsArgs.Rent().Obj(weather, J.WeatherOptions).ToJson());

    /// <summary>Captures the current frame as a PNG data URL (<c>data:image/png;base64,…</c>).</summary>
    public async ValueTask<string> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
        => await _c.CallAsync("scene.screenshot", JsArgs.Empty, J.String, cancellationToken).ConfigureAwait(false) ?? "";

    /// <summary>Captures the current frame as PNG bytes.</summary>
    public async ValueTask<byte[]> CaptureScreenshotPngAsync(CancellationToken cancellationToken = default)
    {
        var dataUrl = await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        var comma = dataUrl.IndexOf(',');
        return comma < 0 ? [] : Convert.FromBase64String(dataUrl[(comma + 1)..]);
    }

    /// <summary>Picks whatever is at a screen position (CSS pixels).</summary>
    public ValueTask<PickResult?> PickAsync(double x, double y)
        => _c.CallAsync("scene.pick", JsArgs.Rent().Num(x).Num(y).ToJson(), J.PickResult);

    /// <summary>Converts a geographic position to screen coordinates (null if not visible).</summary>
    public ValueTask<Cartesian2?> ToScreenAsync(GeoPoint position)
        => _c.CallAsync("scene.toScreen", JsArgs.Rent().Obj(position, J.GeoPoint).ToJson(), J.NullableCartesian2);

    /// <summary>Changes which events are forwarded to .NET at runtime.</summary>
    public ValueTask ConfigureEventsAsync(CesiumEventOptions events)
        => _c.CallVoidAsync("events.configure", JsArgs.Rent().Obj(events, J.CesiumEventOptions).ToJson());
}

/// <summary>Interactive drawing, measuring and editing.</summary>
public sealed class DrawingApi
{
    private readonly CesiumController _c;
    private TaskCompletionSource<DrawResult?>? _activeDraw;
    internal DrawingApi(CesiumController controller) => _c = controller;
    private static CesiumJsonContext J => CesiumJsonContext.Default;

    /// <summary>
    /// Starts an interactive drawing. Left-click adds vertices, double-click or right-click finishes,
    /// Esc cancels. The result is raised through <see cref="CesiumController.DrawCompleted"/> and returned here.
    /// </summary>
    public async ValueTask<DrawResult?> DrawAsync(DrawOptions options, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DrawResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Starting a new drawing replaces the previous one (the bridge cancels it silently): complete the old call with null.
        Interlocked.Exchange(ref _activeDraw, tcs)?.TrySetResult(null);
        void OnDone(object? s, DrawResult r) => tcs.TrySetResult(r);
        void OnCancel(object? s, EventArgs e) => tcs.TrySetResult(null);
        _c.DrawCompleted += OnDone;
        _c.DrawCancelled += OnCancel;
        try
        {
            await using var reg = cancellationToken.Register(() => { _ = CancelAsync(); tcs.TrySetResult(null); });
            await _c.CallVoidAsync("draw.start", JsArgs.Rent().Obj(options, J.DrawOptions).ToJson(), cancellationToken).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _c.DrawCompleted -= OnDone;
            _c.DrawCancelled -= OnCancel;
        }
    }

    /// <summary>Cancels an active drawing.</summary>
    public ValueTask CancelAsync() => _c.CallVoidAsync("draw.cancel", JsArgs.Empty);

    /// <summary>Removes all shapes and labels created by drawing/measuring.</summary>
    public ValueTask ClearAsync() => _c.CallVoidAsync("draw.clear", JsArgs.Empty);

    /// <summary>
    /// Makes an entity's vertices draggable (polylines, polygons, points, billboards, models).
    /// Changes are reported through <see cref="CesiumController.EntityEdited"/>.
    /// </summary>
    public ValueTask BeginEditAsync(string entityId) => _c.CallVoidAsync("edit.begin", JsArgs.Rent().Str(entityId).ToJson());

    /// <summary>Ends editing mode.</summary>
    public ValueTask EndEditAsync() => _c.CallVoidAsync("edit.end", JsArgs.Empty);
}
