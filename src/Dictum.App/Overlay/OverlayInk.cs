using Avalonia;
using Avalonia.Media;
using Dictum.Core.Configuration;

namespace Dictum.App.Overlay;

/// <summary>What a stroke or a fill is painted with.</summary>
public enum Ink
{
    Waves,
    Cleaning,
    Meter,
    Done,
    Cancelled,
    Error,
    ErrorIcon,
    Shadow,
    Track,
    Chip,
}

/// <summary>
/// One palette's brushes and pens, cached by alpha in 1/64 steps: after the first frames a frame allocates only its
/// geometry. Made and used on the UI thread by one <see cref="OverlayCanvas"/>.
/// </summary>
public sealed class OverlayInk(OverlayPalette palette)
{
    private const int Steps = 64;
    private readonly PaletteSpec _spec = OverlayTokens.Palette(palette);
    private readonly Dictionary<(Ink, int), IBrush> _brushes = [];
    private readonly Dictionary<(Ink, int, double), IPen> _pens = [];

    public OverlayPalette Palette { get; } = palette;

    public double GlowOpacity => _spec.GlowOpacity;

    public IBrush Brush(Ink ink, double alpha)
    {
        var step = Step(alpha);
        if (!_brushes.TryGetValue((ink, step), out var brush))
        {
            brush = Create(ink, (double)step / Steps);
            _brushes[(ink, step)] = brush;
        }

        return brush;
    }

    public IPen Pen(Ink ink, double alpha, double width)
    {
        var step = Step(alpha);
        if (!_pens.TryGetValue((ink, step, width), out var pen))
        {
            pen = new Pen(Brush(ink, alpha), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            _pens[(ink, step, width)] = pen;
        }

        return pen;
    }

    private static int Step(double alpha) => (int)Math.Round(Math.Clamp(alpha, 0, 1) * Steps);

    private IBrush Create(Ink ink, double opacity)
    {
        const double left = OverlayTokens.GradientInset;
        const double right = OverlayTokens.Width - OverlayTokens.GradientInset;
        const double meterLeft = (OverlayTokens.Width - OverlayTokens.MeterWidth) / 2;
        const double meterRight = (OverlayTokens.Width + OverlayTokens.MeterWidth) / 2;
        return ink switch
        {
            Ink.Waves => Gradient(opacity, left, right, (_spec.Wave1, 0), (_spec.Wave2, 0.5), (_spec.Wave3, 1)),
            Ink.Cleaning => Gradient(opacity, left, right, (_spec.Cleaning1, 0), (_spec.Cleaning2, 1)),
            Ink.Meter => Gradient(opacity, meterLeft, meterRight, (_spec.Wave1, 0), (_spec.Wave3, 1)),
            _ => new SolidColorBrush(Color.FromUInt32(Solid(ink)), opacity),
        };
    }

    private static uint Solid(Ink ink) => ink switch
    {
        Ink.Done => OverlayTokens.DoneStroke,
        Ink.Cancelled => OverlayTokens.CancelledStroke,
        Ink.Error => OverlayTokens.ErrorStroke,
        Ink.ErrorIcon => OverlayTokens.ErrorIcon,
        Ink.Track => OverlayTokens.Track,
        Ink.Chip => OverlayTokens.ChipBackground,
        _ => OverlayTokens.Shadow,
    };

    private static LinearGradientBrush Gradient(double opacity, double x0, double x1, params (uint Colour, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            Opacity = opacity,
            StartPoint = new RelativePoint(x0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(x1, 0, RelativeUnit.Absolute),
        };
        foreach (var (colour, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromUInt32(colour), offset));
        }

        return brush;
    }
}
