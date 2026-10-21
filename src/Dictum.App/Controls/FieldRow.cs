using Avalonia;
using Avalonia.Controls;

namespace Dictum.App.Controls;

/// <summary>
/// A labelled field of an editor row: the label, its first child, in a column of <see cref="LabelWidth"/> beside the
/// field, its second child. When the field would get less than <see cref="MinFieldWidth"/>, as in a narrow Settings
/// window, the field wraps under its label and takes the row's whole width, rather than being cut off at the side; so
/// it does when a word of the label is wider than the label's column, which would break the word across lines, and the
/// label then takes the row's width too.
/// </summary>
public sealed class FieldRow : Panel
{
    public static readonly StyledProperty<double> LabelWidthProperty = AvaloniaProperty.Register<FieldRow, double>(nameof(LabelWidth), 140);

    public static readonly StyledProperty<double> MinFieldWidthProperty = AvaloniaProperty.Register<FieldRow, double>(nameof(MinFieldWidth), 280);

    /// <summary>The gap between the label and the field beside it.</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<FieldRow, double>(nameof(ColumnSpacing), 16);

    /// <summary>The gap between the label and the field under it.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<FieldRow, double>(nameof(RowSpacing), 8);

    /// <summary>
    /// The layout the last measure chose. Arrange keeps it: layout rounding or DPI scaling can arrange the row a little
    /// narrower than it was measured, and a field arranged under its label after a measure beside it would overflow the
    /// height the row asked for and cover the next row.
    /// </summary>
    private bool _beside = true;

    static FieldRow() => AffectsMeasure<FieldRow>(LabelWidthProperty, MinFieldWidthProperty, ColumnSpacingProperty, RowSpacingProperty);

    public double LabelWidth
    {
        get => GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public double MinFieldWidth
    {
        get => GetValue(MinFieldWidthProperty);
        set => SetValue(MinFieldWidthProperty, value);
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

    /// <summary>Whether the field sits beside its label: the layout the last measure chose.</summary>
    public bool IsBeside => _beside;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (label, field) = Parts();
        _beside = availableSize.Width >= LabelWidth + ColumnSpacing + MinFieldWidth;
        if (_beside)
        {
            label?.Measure(availableSize.WithWidth(LabelWidth));
            _beside = !WordBreaks.AnyIn(label);
        }

        if (_beside)
        {
            field?.Measure(availableSize.WithWidth(availableSize.Width - LabelWidth - ColumnSpacing));
            return new Size(LabelWidth + ColumnSpacing + DesiredWidth(field), Math.Max(DesiredHeight(label), DesiredHeight(field)));
        }

        label?.Measure(availableSize);
        field?.Measure(availableSize);
        return new Size(Math.Max(DesiredWidth(label), DesiredWidth(field)), DesiredHeight(label) + RowSpacing + DesiredHeight(field));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (label, field) = Parts();
        if (_beside)
        {
            label?.Arrange(new Rect(0, 0, LabelWidth, finalSize.Height));
            field?.Arrange(new Rect(LabelWidth + ColumnSpacing, 0, finalSize.Width - LabelWidth - ColumnSpacing, finalSize.Height));
        }
        else
        {
            label?.Arrange(new Rect(0, 0, finalSize.Width, DesiredHeight(label)));
            field?.Arrange(new Rect(0, DesiredHeight(label) + RowSpacing, finalSize.Width, DesiredHeight(field)));
        }

        return finalSize;
    }

    private (Control? Label, Control? Field) Parts() => (Children.Count > 0 ? Children[0] : null, Children.Count > 1 ? Children[1] : null);

    private static double DesiredWidth(Control? child) => child?.DesiredSize.Width ?? 0;

    private static double DesiredHeight(Control? child) => child?.DesiredSize.Height ?? 0;
}
