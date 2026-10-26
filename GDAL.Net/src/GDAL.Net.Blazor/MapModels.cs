using GDAL.Net.Rendering;

namespace GDAL.Net.Blazor;

/// <summary>A raster layer shown by <see cref="GdalMap"/>. The map never disposes the dataset.</summary>
/// <param name="Dataset">Any north-up raster (warp rotated rasters first).</param>
/// <param name="Options">How the raster is rendered (ramp, stretch, bands, hillshade, opacity…).</param>
/// <param name="Title">Optional layer title.</param>
public sealed record MapRaster(Dataset Dataset, RenderOptions Options, string? Title = null);

/// <summary>Raised when the user clicks a vector feature.</summary>
/// <param name="Layer">The layer the feature belongs to.</param>
/// <param name="Feature">The clicked feature.</param>
/// <param name="X">World X of the click.</param>
/// <param name="Y">World Y of the click.</param>
public sealed record MapFeatureClick(StyledVector Layer, VectorFeature Feature, double X, double Y);

/// <summary>Raised when the user clicks the map (with or without a feature under the cursor).</summary>
public sealed record MapClick(double X, double Y, IReadOnlyList<MapFeatureClick> Hits);
