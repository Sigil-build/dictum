using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Resources;
using Dictum.App.Settings;

namespace Dictum.App.Controls;

/// <summary>The 30-segment input meter (design "Level meter"): green to 23, amber 24–27, red 28–30.</summary>
public sealed class LevelMeterBar : Control
{
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<LevelMeterBar, double>(nameof(Level));

    private const double Gap = 3;
    private const double BarHeight = 10;

    static LevelMeterBar() => AffectsRender<LevelMeterBar>(LevelProperty);

    public LevelMeterBar() => AutomationProperties.SetName(this, Strings.SettingsInputLevel);

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, BarHeight);

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
            var key = i >= lit ? "color.border" : i >= 27 ? "color.meter.clip" : i >= 23 ? "color.meter.hot" : "color.meter.good";
            context.DrawRectangle(ThemeBrush.Find(this, key), null, new RoundedRect(new Rect(i * (width + Gap), 0, width, BarHeight), 2));
        }
    }
}
