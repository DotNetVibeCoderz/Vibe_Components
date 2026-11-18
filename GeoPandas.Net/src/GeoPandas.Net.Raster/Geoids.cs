using GeoPandas.Net.Geometry.Crs;

namespace GeoPandas.Net.Raster;

/// <summary>GeoTIFF geoid grids (PROJ CDN, e.g. us_nga_egm08_25.tif, id_big_InaGEOID2020.tif) as <see cref="GeoidModel"/>.</summary>
public static class Geoids
{
    private static int s_registered;

    /// <summary>Registers the ".tif"/".tiff" loader with <see cref="GeoidModel.Load"/>.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        GeoidModel.RegisterLoader(".tif", LoadGeoTiff);
        GeoidModel.RegisterLoader(".tiff", LoadGeoTiff);
    }

    /// <summary>Reads band 1 of a lon/lat GeoTIFF; pixel centres become grid nodes.</summary>
    public static GeoidModel LoadGeoTiff(string path) => FromRaster(GeoTiff.Read(path), Path.GetFileName(path));

    public static GeoidModel FromRaster(Raster r, string name)
    {
        var t = r.Transform;
        double west = t.OriginX + t.PixelWidth / 2, lonInc = t.PixelWidth, latInc = Math.Abs(t.PixelHeight);
        double north = t.OriginY + t.PixelHeight / 2, south = north - (r.Height - 1) * latInc;
        var v = new float[r.Width * r.Height];
        var band = r.Bands[0];
        for (int row = 0; row < r.Height; row++)
            for (int c = 0; c < r.Width; c++)
            {
                float x = band[row * r.Width + c];
                v[(r.Height - 1 - row) * r.Width + c] = r.IsNoData(x) ? float.NaN : x;
            }
        bool global = Math.Abs(r.Width * lonInc - 360) < lonInc * 0.51;
        return new GeoidModel(name, west, south, lonInc, latInc, r.Width, r.Height, v, global);
    }
}
