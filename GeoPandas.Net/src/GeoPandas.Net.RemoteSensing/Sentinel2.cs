using System.Globalization;
using System.Xml.Linq;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Raster;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.RemoteSensing;

/// <summary>
/// Copernicus Sentinel-2 Level-1C / Level-2A product in SAFE format: product and tile metadata (MTD_MSIL1C/2A.xml, MTD_TL.xml),
/// JPEG 2000 bands decoded with CoreJ2K, georeferenced from the tile geocoding, scaled to reflectance with the processing-baseline offsets,
/// and the L2A scene classification (SCL) cloud mask.
/// </summary>
public sealed class Sentinel2Product
{
    private readonly string _root;
    private readonly List<string> _images;   // relative paths without extension
    private readonly XDocument _tile;

    private Sentinel2Product(string root, XDocument product, XDocument tile, List<string> images)
    {
        _root = root; _tile = tile; _images = images;
        string Find(XDocument d, string name) => d.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
        Level = Find(product, "PRODUCT_TYPE") is { Length: > 0 } t ? t : product.Root!.Name.LocalName.Contains("2A") ? "S2MSI2A" : "S2MSI1C";
        Spacecraft = Find(product, "SPACECRAFT_NAME");
        ProcessingBaseline = Find(product, "PROCESSING_BASELINE");
        SensingTime = DateTime.TryParse(Find(product, "PRODUCT_START_TIME"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var st) ? st : default;
        double q = double.TryParse(Find(product, IsLevel2A ? "BOA_QUANTIFICATION_VALUE" : "QUANTIFICATION_VALUE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var qv) ? qv : 10000;
        Quantification = q;
        var offsets = product.Descendants().Where(e => e.Name.LocalName is "BOA_ADD_OFFSET" or "RADIO_ADD_OFFSET")
            .ToDictionary(e => int.Parse(e.Attribute("band_id")!.Value, CultureInfo.InvariantCulture), e => double.Parse(e.Value, CultureInfo.InvariantCulture));
        Offsets = offsets;
        var code = Find(tile, "HORIZONTAL_CS_CODE");
        Crs = code.Length > 0 && CoordinateReferenceSystem.TryParse(code, out var c) ? c : null;
        TileId = Find(tile, "TILE_ID");
        CloudCover = double.TryParse(Find(product, "Cloud_Coverage_Assessment"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cc) ? cc : null;
    }

    /// <summary>Opens a .SAFE folder (or its MTD_MSIL*.xml).</summary>
    public static Sentinel2Product Open(string safeOrMtd)
    {
        string mtd = Directory.Exists(safeOrMtd)
            ? Directory.EnumerateFiles(safeOrMtd, "MTD_MSIL*.xml").FirstOrDefault() ?? throw new FileNotFoundException($"No MTD_MSIL*.xml in '{safeOrMtd}'.")
            : safeOrMtd;
        string root = Path.GetDirectoryName(Path.GetFullPath(mtd))!;
        var product = XDocument.Load(mtd);
        var images = product.Descendants().Where(e => e.Name.LocalName == "IMAGE_FILE").Select(e => e.Value.Trim()).ToList();
        var tileMtd = Directory.EnumerateFiles(Path.Combine(root, "GRANULE"), "MTD_TL.xml", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("GRANULE/*/MTD_TL.xml not found.");
        return new Sentinel2Product(root, product, XDocument.Load(tileMtd), images);
    }

    public string Level { get; }
    public bool IsLevel2A => Level.Contains("2A", StringComparison.OrdinalIgnoreCase);
    public string Spacecraft { get; }
    public string ProcessingBaseline { get; }
    public DateTime SensingTime { get; }
    public string TileId { get; }
    public double? CloudCover { get; }
    public CoordinateReferenceSystem? Crs { get; }
    public double Quantification { get; }
    /// <summary>Additive offsets per band index (processing baseline ≥ 04.00: −1000 DN).</summary>
    public IReadOnlyDictionary<int, double> Offsets { get; }

    private static readonly string[] BandOrder = ["B01", "B02", "B03", "B04", "B05", "B06", "B07", "B08", "B8A", "B09", "B10", "B11", "B12"];

    /// <summary>Band names available in the product ("B04", "B8A", "SCL", "TCI"…), with their resolutions for L2A.</summary>
    public IReadOnlyList<string> Bands => _images.Select(i => Path.GetFileName(i)).ToList();

    private string ImagePath(string band, int? resolution)
    {
        string? rel = _images.FirstOrDefault(i =>
        {
            var n = Path.GetFileName(i);
            return n.Contains($"_{band}_", StringComparison.OrdinalIgnoreCase) && (resolution is null || n.EndsWith($"_{resolution}m", StringComparison.Ordinal))
                || n.EndsWith($"_{band}", StringComparison.OrdinalIgnoreCase);
        });
        if (rel is null && resolution is null)
            foreach (var r in new[] { 10, 20, 60 })
                if ((rel = _images.FirstOrDefault(i => Path.GetFileName(i).EndsWith($"_{band}_{r}m", StringComparison.OrdinalIgnoreCase))) is not null) break;
        if (rel is null) throw new KeyNotFoundException($"Band '{band}'{(resolution is null ? "" : $" at {resolution} m")} is not in the product.");
        return Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar) + ".jp2");
    }

    /// <summary>Digital numbers of a band (JPEG 2000 decoded) with the tile georeferencing.</summary>
    public RasterGrid Band(string band, int? resolution = null)
    {
        var path = ImagePath(band, resolution);
        var img = CoreJ2K.J2kImage.FromFile(path);
        int w = img.Width, h = img.Height;
        var comp = img.GetComponent(0);
        var data = new float[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = comp[i];
        // tile geocoding at the band resolution (pixel size from the image width)
        var geos = _tile.Descendants().Where(e => e.Name.LocalName == "Geoposition").ToList();
        var sizes = _tile.Descendants().Where(e => e.Name.LocalName == "Size").ToList();
        int res = sizes.Where(s => int.Parse(s.Elements().First(x => x.Name.LocalName == "NCOLS").Value, CultureInfo.InvariantCulture) == w)
            .Select(s => int.Parse(s.Attribute("resolution")!.Value, CultureInfo.InvariantCulture)).FirstOrDefault(resolution ?? 10);
        var g = geos.FirstOrDefault(x => x.Attribute("resolution")?.Value == res.ToString(CultureInfo.InvariantCulture)) ?? geos.First();
        double V(string n) => double.Parse(g.Elements().First(e => e.Name.LocalName == n).Value, CultureInfo.InvariantCulture);
        var raster = new RasterGrid(w, h, [data], new GeoTransform(V("ULX"), V("XDIM"), 0, V("ULY"), 0, V("YDIM")), Crs, 0) { BandNames = [band] };
        return raster;
    }

    /// <summary>Reflectance: (DN + offset) / quantification (10 000), nodata 0 → NaN.</summary>
    public RasterGrid Reflectance(string band, int? resolution = null)
    {
        var dn = Band(band, resolution);
        int idx = Array.IndexOf(BandOrder, band.ToUpperInvariant());
        double offset = idx >= 0 && Offsets.TryGetValue(idx, out var o) ? o : 0;
        var r = dn.Map(v => v == 0 ? float.NaN : (float)((v + offset) / Quantification));
        r.BandNames = [band];
        return r;
    }

    /// <summary>L2A clear-sky mask (1 = clear) from the scene classification: no data, saturated, cloud shadow, cloud medium/high, cirrus, (snow).</summary>
    public RasterGrid ClearMask(bool maskSnow = false)
    {
        if (!IsLevel2A) throw new InvalidOperationException("The SCL layer exists in Level-2A products only.");
        var scl = Band("SCL", 20);
        return scl.Map(v => v is 0 or 1 or 3 or 8 or 9 or 10 || (maskSnow && v == 11) ? 0f : 1f);
    }

    /// <summary>NDVI = (B08 − B04) / (B08 + B04) at 10 m.</summary>
    public RasterGrid Ndvi()
    {
        var nir = Reflectance("B08", IsLevel2A ? 10 : null); var red = Reflectance("B04", IsLevel2A ? 10 : null);
        var n = RasterGrid.Combine(nir, red, (a, b) => a + b == 0 ? float.NaN : (a - b) / (a + b));
        n.BandNames = ["ndvi"];
        return n;
    }

    public override string ToString() => $"{Spacecraft} {Level} tile {TileId} {SensingTime:yyyy-MM-dd HH:mm}Z baseline {ProcessingBaseline} cloud {CloudCover:F1}%";
}
