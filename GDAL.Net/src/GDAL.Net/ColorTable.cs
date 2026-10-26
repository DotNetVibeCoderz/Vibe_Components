using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>A raster palette (GDALColorTable).</summary>
public sealed unsafe class ColorTable : GdalObject
{
    internal ColorTable(nint handle) : base(new GdalSafeHandle(handle, &Release)) { }

    private static void Release(nint h) => GdalNative.GDALDestroyColorTable(h);

    public ColorTable(PaletteInterpretation interpretation = PaletteInterpretation.Rgb)
        : this(GdalNative.GDALCreateColorTable((int)interpretation)) { }

    public PaletteInterpretation Interpretation => (PaletteInterpretation)GdalNative.GDALGetPaletteInterpretation(Handle);

    public int Count => GdalNative.GDALGetColorEntryCount(Handle);

    public ColorEntry this[int index]
    {
        get
        {
            var e = GdalNative.GDALGetColorEntry(Handle, index);
            return e == null ? default : new ColorEntry(e->c1, e->c2, e->c3, e->c4);
        }
        set
        {
            var e = new GDALColorEntry { c1 = value.C1, c2 = value.C2, c3 = value.C3, c4 = value.C4 };
            GdalNative.GDALSetColorEntry(Handle, index, &e);
        }
    }

    /// <summary>All entries.</summary>
    public IReadOnlyList<ColorEntry> Entries => Enumerable.Range(0, Count).Select(i => this[i]).ToArray();
}

/// <summary>A raster attribute table (thematic rasters: class names, colors, pixel counts, ...).</summary>
public sealed unsafe class RasterAttributeTable : GdalObject
{
    internal RasterAttributeTable(GdalSafeHandle handle) : base(handle) { }

    private static void Release(nint h) => GdalNative.GDALDestroyRasterAttributeTable(h);

    /// <summary>Creates a new, empty attribute table (assign it with <see cref="RasterBand.AttributeTable"/>).</summary>
    public RasterAttributeTable() : base(new GdalSafeHandle(GdalNative.GDALCreateRasterAttributeTable(), &Release)) { }

    public int ColumnCount => GdalNative.GDALRATGetColumnCount(Handle);

    public int RowCount
    {
        get => GdalNative.GDALRATGetRowCount(Handle);
        set => GdalNative.GDALRATSetRowCount(Handle, value);
    }

    public string GetColumnName(int column) => Interop.PtrToString(GdalNative.GDALRATGetNameOfCol(Handle, column)) ?? string.Empty;
    public RatFieldType GetColumnType(int column) => (RatFieldType)GdalNative.GDALRATGetTypeOfCol(Handle, column);
    public RatFieldUsage GetColumnUsage(int column) => (RatFieldUsage)GdalNative.GDALRATGetUsageOfCol(Handle, column);

    public void AddColumn(string name, RatFieldType type, RatFieldUsage usage = RatFieldUsage.Generic)
        => GdalNative.Check(GdalNative.GDALRATCreateColumn(Handle, name, (int)type, (int)usage), $"Create RAT column '{name}'");

    public string GetString(int row, int column) => Interop.PtrToString(GdalNative.GDALRATGetValueAsString(Handle, row, column)) ?? string.Empty;
    public int GetInt(int row, int column) => GdalNative.GDALRATGetValueAsInt(Handle, row, column);
    public double GetDouble(int row, int column) => GdalNative.GDALRATGetValueAsDouble(Handle, row, column);

    public void Set(int row, int column, string value) => GdalNative.GDALRATSetValueAsString(Handle, row, column, value);
    public void Set(int row, int column, int value) => GdalNative.GDALRATSetValueAsInt(Handle, row, column, value);
    public void Set(int row, int column, double value) => GdalNative.GDALRATSetValueAsDouble(Handle, row, column, value);

    /// <summary>Snapshot of the table as rows of strings (column names first).</summary>
    public (string[] Columns, string[][] Rows) ToTable()
    {
        var cols = Enumerable.Range(0, ColumnCount).Select(GetColumnName).ToArray();
        var rows = new string[RowCount][];
        for (var r = 0; r < rows.Length; r++)
        {
            rows[r] = new string[cols.Length];
            for (var c = 0; c < cols.Length; c++) rows[r][c] = GetString(r, c);
        }
        return (cols, rows);
    }
}
