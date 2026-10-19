using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Overlay;
using Dictum.Core.Configuration;

namespace Dictum.App.Controls;

/// <summary>
/// The overlay listening to the simulated voice, drawn over a picture of a desktop: a dark panel with a taskbar
/// strip. It runs the real state machine and canvas on every display frame while it is in a window.
/// </summary>
public sealed class OverlayPreview : Control
{
    public static readonly StyledProperty<OverlayPalette> PaletteProperty = AvaloniaProperty.Register<OverlayPreview, OverlayPalette>(nameof(Palette));

    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<OverlayPreview, double>(nameof(Zoom), 1);

    public static readonly StyledProperty<double> OverlayOpacityProperty = AvaloniaProperty.Register<OverlayPreview, double>(nameof(OverlayOpacity), 1);

    private const double Top = 14;
    private const double Gap = 4;
    private const double Taskbar = 36;
    private const double PanelRadius = 8;
    private const double AppIconSize = 18;
    private const double AppIconGap = 12;
    private const double AppIconRadius = 4;

    /// <summary>Desktop shown beside the overlay when the layout sets no width.</summary>
    private const double UnconstrainedPadding = 96;

    private static readonly IBrush Desk = new SolidColorBrush(Color.Parse("#1E1F22"));
    private static readonly IBrush Bar = new SolidColorBrush(Color.Parse("#EB1C1C1E"));
    private static readonly IBrush[] Apps = [new SolidColorBrush(Color.Parse("#4C8DFF")), new SolidColorBrush(Color.Parse("#E8EAF0")), new SolidColorBrush(Color.Parse("#F2C94C"))];

    private readonly OverlayStateMachine _machine = new();
    private readonly float[] _bands = new float[OverlayTokens.BandCount];
    private TimeSpan? _start;
    private bool _running;

    static OverlayPreview() => AffectsMeasure<OverlayPreview>(ZoomProperty);

    public OverlayPreview()
    {
        Canvas = new OverlayCanvas();
        VisualChildren.Add(Canvas);
    }

    public OverlayCanvas Canvas { get; }

    public OverlayPalette Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    /// <summary>The size setting's factor, <see cref="OverlayTokens.SizeScale"/>.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>The opacity setting, 0.3–1.</summary>
    public double OverlayOpacity
    {
        get => GetValue(OverlayOpacityProperty);
        set => SetValue(OverlayOpacityProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Canvas.Zoom = Zoom;
        Canvas.Measure(Size.Infinity);
        var width = double.IsInfinity(availableSize.Width) ? Canvas.DesiredSize.Width + UnconstrainedPadding : availableSize.Width;
        return new Size(width, Top + Canvas.DesiredSize.Height + Gap + Taskbar);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = Canvas.DesiredSize;
        Canvas.Arrange(new Rect((finalSize.Width - size.Width) / 2, Top, size.Width, size.Height));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(bounds, PanelRadius)))
        {
            context.DrawRectangle(Desk, null, bounds);
            context.DrawRectangle(Bar, null, new Rect(0, bounds.Height - Taskbar, bounds.Width, Taskbar));
            var left = (bounds.Width - (Apps.Length * AppIconSize + (Apps.Length - 1) * AppIconGap)) / 2;
            for (var i = 0; i < Apps.Length; i++)
            {
                var icon = new Rect(left + i * (AppIconSize + AppIconGap), bounds.Height - Taskbar + (Taskbar - AppIconSize) / 2, AppIconSize, AppIconSize);
                context.DrawRectangle(Apps[i], null, new RoundedRect(icon, AppIconRadius));
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _running = true;
        _start = null;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _running = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_running)
        {
            return;
        }

        if (_start is null)
        {
            _start = now;
            _machine.Recording(now, []);
        }

        _machine.Palette = Palette;
        var rms = SimulatedVoice.Sample((now - _start.Value).TotalSeconds, _bands);
        var scene = _machine.Advance(now, rms, _bands);
        Canvas.Scene = scene;
        Canvas.LayerOpacity = OverlayOpacity * scene.Appear;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }
}
