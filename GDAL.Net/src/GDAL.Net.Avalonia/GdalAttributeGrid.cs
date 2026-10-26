using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using GDAL.Net.Rendering;

namespace GDAL.Net.Avalonia;

/// <summary>
/// Attribute table backed by a <see cref="DataGrid"/>: show a <see cref="VectorData"/> snapshot (FID + every field) or any
/// tabular result (<see cref="Columns"/> + <see cref="Rows"/>). Sortable, resizable, virtualized.
/// Requires the DataGrid theme: <c>&lt;StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"/&gt;</c>
/// (included by <c>GdalNetTheme</c>).
/// </summary>
public class GdalAttributeGrid : UserControl
{
    public static readonly StyledProperty<VectorData?> DataProperty =
        AvaloniaProperty.Register<GdalAttributeGrid, VectorData?>(nameof(Data));

    public static readonly StyledProperty<IReadOnlyList<string>?> ColumnsProperty =
        AvaloniaProperty.Register<GdalAttributeGrid, IReadOnlyList<string>?>(nameof(Columns));

    public static readonly StyledProperty<IEnumerable<IReadOnlyList<string>>?> RowsProperty =
        AvaloniaProperty.Register<GdalAttributeGrid, IEnumerable<IReadOnlyList<string>>?>(nameof(Rows));

    /// <summary>Maximum number of rows loaded from <see cref="Data"/>.</summary>
    public static readonly StyledProperty<int> MaxRowsProperty =
        AvaloniaProperty.Register<GdalAttributeGrid, int>(nameof(MaxRows), 5000);

    public VectorData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IReadOnlyList<string>? Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public IEnumerable<IReadOnlyList<string>>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public int MaxRows { get => GetValue(MaxRowsProperty); set => SetValue(MaxRowsProperty, value); }

    private readonly DataGrid _grid;

    /// <summary>The inner DataGrid (for further customization).</summary>
    public DataGrid Grid => _grid;

    /// <summary>Raised when the user selects a row of a <see cref="Data"/> table.</summary>
    public event EventHandler<VectorFeature?>? FeatureSelected;

    public GdalAttributeGrid()
    {
        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserResizeColumns = true,
            CanUserSortColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
        };
        _grid.Classes.Add("gdal-grid");
        _grid.SelectionChanged += (_, _) =>
        {
            if (Data is { } d && _grid.SelectedItem is GridRow row && row.Index < d.Features.Count)
            {
                FeatureSelected?.Invoke(this, d.Features[row.Index]);
            }
        };
        Content = _grid;
    }

    /// <summary>One displayed row (cells as strings, plus the source index).</summary>
    public sealed class GridRow(int index, string[] cells)
    {
        public int Index { get; } = index;
        public string[] Cells { get; } = cells;
        public string this[int i] => i < Cells.Length ? Cells[i] : string.Empty;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty || change.Property == ColumnsProperty || change.Property == RowsProperty || change.Property == MaxRowsProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        IReadOnlyList<string> columns;
        List<GridRow> rows;
        if (Data is { } data)
        {
            columns = ["FID", .. data.Fields.Select(f => f.Name)];
            rows = data.Features.Take(MaxRows)
                .Select((f, i) => new GridRow(i, [f.Id.ToString(CultureInfo.InvariantCulture), .. data.Fields.Select(fd => Format(f[fd.Name]))]))
                .ToList();
        }
        else
        {
            columns = Columns ?? [];
            rows = (Rows ?? []).Take(MaxRows).Select((r, i) => new GridRow(i, [.. r])).ToList();
        }

        _grid.ItemsSource = null;
        _grid.Columns.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            var numeric = rows.Count > 0 && rows.Take(20).All(r => r[i].Length == 0 || double.TryParse(r[i].Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i],
                Binding = new Binding($"Cells[{i}]"),
                CanUserSort = true,
                Width = DataGridLength.Auto,
                CellStyleClasses = { numeric ? "num" : "text" },
            });
        }
        _grid.ItemsSource = rows;
    }

    /// <summary>Formats an attribute value for display.</summary>
    public static string Format(object? v) => v switch
    {
        null => "∅",
        double d => d.ToString("#,0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("#,0.###", CultureInfo.InvariantCulture),
        long l => l.ToString("#,0", CultureInfo.InvariantCulture),
        int i => i.ToString("#,0", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        byte[] b => $"<{b.Length} bytes>",
        _ => v.ToString() ?? string.Empty,
    };
}
