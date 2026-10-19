using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Resources;
using Dictum.App.Settings;

namespace Dictum.App.Controls;

/// <summary>
/// The segmented input meter: the lit segments are green, amber from <see cref="HotFrom"/> and red from
/// <see cref="ClipFrom"/> (zero-based).
/// </summary>
public sealed class LevelMeterBar : Control
{
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<LevelMeterBar, double>(nameof(Level));

    private const double Gap = 3;
    private const double BarHeight = 10;
    private const double SegmentRadius = 2;
    private const int HotFrom = 23;
    private const int ClipFrom = 27;

    /// <summary>The width asked for when the layout sets no limit.</summary>
    private const double UnconstrainedWidth = 300;

    static LevelMeterBar() => AffectsRender<LevelMeterBar>(LevelProperty);

    public LevelMeterBar() => AutomationProperties.SetName(this, Strings.SettingsInputLevel);

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? UnconstrainedWidth : availableSize.Width, BarHeight);

    public override void Render(DrawingContext context)
    {
        var width = (Bounds.Width - Gap * (InputMeter.Segments - 1)) / InputMeter.Segments;
        if (width <= 0)
        {
            return;
        }

        var lit = InputMeter.LitFor(Level);
        for (var i = 0; i < InputMeter.Segments; i++)
        {
            var key = i >= lit ? "color.border" : i >= ClipFrom ? "color.meter.clip" : i >= HotFrom ? "color.meter.hot" : "color.meter.good";
            context.DrawRectangle(ThemeBrush.Find(this, key), null, new RoundedRect(new Rect(i * (width + Gap), 0, width, BarHeight), SegmentRadius));
        }
    }
}
