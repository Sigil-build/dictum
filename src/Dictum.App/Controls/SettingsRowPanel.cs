using Avalonia;
using Avalonia.Controls;

namespace Dictum.App.Controls;

/// <summary>
/// The texts and the control of a <see cref="SettingsRow"/>: the label, its first child, wrapping between its words
/// beside the control, its second child, which keeps the width it asks for. When beside the control the label would
/// have to break a word across lines, as in a narrow window or a font wider than Segoe UI, the control goes under the
/// label, at its left edge, and the label takes the row's width, as a <see cref="FieldRow"/> puts its field under its label.
/// </summary>
public sealed class SettingsRowPanel : Panel
{
    /// <summary>The gap between the label and the control beside it.</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<SettingsRowPanel, double>(nameof(ColumnSpacing), 16);

    /// <summary>The gap between the label and the control under it.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<SettingsRowPanel, double>(nameof(RowSpacing), 8);

    /// <summary>The layout the last measure chose, which arrange keeps, as in <see cref="FieldRow"/>.</summary>
    private bool _beside = true;

    static SettingsRowPanel() => AffectsMeasure<SettingsRowPanel>(ColumnSpacingProperty, RowSpacingProperty);

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

    /// <summary>Whether the control sits beside the label: the layout the last measure chose.</summary>
    public bool IsBeside => _beside;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (label, control) = Parts();

        // The control asks for its own width, as in a grid's Auto column; the label gets the rest.
        control?.Measure(availableSize.WithWidth(double.PositiveInfinity));
        var room = availableSize.Width - DesiredWidth(control) - ColumnSpacing;
        label?.Measure(availableSize.WithWidth(Math.Max(0, room)));
        _beside = room > 0 && !WordBreaks.AnyIn(label);
        if (_beside)
        {
            return new Size(DesiredWidth(label) + ColumnSpacing + DesiredWidth(control), Math.Max(DesiredHeight(label), DesiredHeight(control)));
        }

        label?.Measure(availableSize);
        control?.Measure(availableSize);
        return new Size(Math.Max(DesiredWidth(label), DesiredWidth(control)), DesiredHeight(label) + RowSpacing + DesiredHeight(control));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (label, control) = Parts();
        var width = Math.Min(DesiredWidth(control), finalSize.Width);
        if (_beside)
        {
            label?.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - width - ColumnSpacing), finalSize.Height));
            control?.Arrange(new Rect(finalSize.Width - width, 0, width, finalSize.Height));
        }
        else
        {
            label?.Arrange(new Rect(0, 0, finalSize.Width, DesiredHeight(label)));
            control?.Arrange(new Rect(0, DesiredHeight(label) + RowSpacing, width, DesiredHeight(control)));
        }

        return finalSize;
    }

    private (Control? Label, Control? Control) Parts() => (Children.Count > 0 ? Children[0] : null, Children.Count > 1 ? Children[1] : null);

    private static double DesiredWidth(Control? child) => child?.DesiredSize.Width ?? 0;

    private static double DesiredHeight(Control? child) => child?.DesiredSize.Height ?? 0;
}
