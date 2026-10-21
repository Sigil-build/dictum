using Avalonia;
using Avalonia.Controls;

namespace Dictum.App.Controls;

/// <summary>
/// The fields of a form in two equal columns, row by row in the order of its children, or in one column when two would
/// each be narrower than <see cref="MinColumnWidth"/>, as in a narrow Settings window. Hidden children take no place.
/// </summary>
public sealed class FormColumns : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty = AvaloniaProperty.Register<FormColumns, double>(nameof(MinColumnWidth), 220);

    public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<FormColumns, double>(nameof(ColumnSpacing), 12);

    public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<FormColumns, double>(nameof(RowSpacing), 12);

    static FormColumns() => AffectsMeasure<FormColumns>(MinColumnWidthProperty, ColumnSpacingProperty, RowSpacingProperty);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>How many columns fit across this width: two, or one when two would be too narrow.</summary>
    public int ColumnsFor(double width) => (width - ColumnSpacing) / 2 >= MinColumnWidth ? 2 : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnsFor(availableSize.Width);
        var column = ColumnWidth(availableSize.Width, columns);
        var shown = Shown();
        foreach (var child in shown)
        {
            child.Measure(availableSize.WithWidth(column));
        }

        var widest = shown.Count == 0 ? 0 : shown.Max(c => c.DesiredSize.Width);
        var rows = Rows(shown, columns);
        return new Size(widest * columns + ColumnSpacing * (columns - 1), rows.Sum() + RowSpacing * Math.Max(0, rows.Count - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsFor(finalSize.Width);
        var column = ColumnWidth(finalSize.Width, columns);
        var shown = Shown();
        var rows = Rows(shown, columns);
        var top = 0.0;
        for (var row = 0; row < rows.Count; row++)
        {
            for (var i = 0; i < columns && row * columns + i < shown.Count; i++)
            {
                shown[row * columns + i].Arrange(new Rect(i * (column + ColumnSpacing), top, column, rows[row]));
            }

            top += rows[row] + RowSpacing;
        }

        return finalSize;
    }

    private double ColumnWidth(double width, int columns) => Math.Max(0, (width - ColumnSpacing * (columns - 1)) / columns);

    private List<Control> Shown() => [.. Children.Where(c => c.IsVisible)];

    /// <summary>Each row's height: its tallest child.</summary>
    private static List<double> Rows(List<Control> shown, int columns) =>
        [.. shown.Chunk(columns).Select(row => row.Max(c => c.DesiredSize.Height))];
}
