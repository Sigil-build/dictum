using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Resources;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Controls;

/// <summary>
/// The 24 px round badge of a status row (design "Status row"): a check, a triangle or a cross on the status
/// colours, so the state never rests on colour alone. It is named OK / Warning / Error for screen readers.
/// </summary>
public sealed class StatusBadge : Control
{
    public static readonly StyledProperty<HealthLevel> LevelProperty = AvaloniaProperty.Register<StatusBadge, HealthLevel>(nameof(Level));

    private Geometry? _check;
    private Geometry? _triangle;
    private Geometry? _cross;

    static StatusBadge() => AffectsRender<StatusBadge>(LevelProperty);

    public StatusBadge() => AutomationProperties.SetName(this, Strings.StatusOk);

    public HealthLevel Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(24, 24);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty)
        {
            AutomationProperties.SetName(this, Level switch
            {
                HealthLevel.Ok => Strings.StatusOk,
                HealthLevel.Warning => Strings.StatusWarning,
                _ => Strings.StatusError,
            });
        }
    }

    public override void Render(DrawingContext context)
    {
        var (background, foreground, glyph, width) = Level switch
        {
            HealthLevel.Ok => ("color.status.ok.bg", "color.status.ok.fg", _check ??= StreamGeometry.Parse("M3.5,7.5 L6,10 L10.5,4.5"), 2.2),
            HealthLevel.Warning => ("color.status.warn.bg", "color.status.warn.fg", _triangle ??= StreamGeometry.Parse("M7,1.5 L12.5,11.5 L1.5,11.5 Z M7,5 L7,8"), 1.8),
            _ => ("color.status.error.bg", "color.status.error.fg", _cross ??= StreamGeometry.Parse("M3.5,3.5 L10.5,10.5 M10.5,3.5 L3.5,10.5"), 2.2),
        };
        context.DrawEllipse(Brush(background), null, new Point(12, 12), 12, 12);
        using (context.PushTransform(Matrix.CreateTranslation(5, 5)))
        {
            context.DrawGeometry(null, new Pen(Brush(foreground), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), glyph);
        }
    }

    private IBrush Brush(string key) => ThemeBrush.Find(this, key);
}
