using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Dictum.App.Tray;

/// <summary>
/// Draws a tray glyph from <see cref="TrayGeometry"/> into a bitmap of the size the panel asks for, so no icon is
/// scaled. Needs Avalonia's render interface (after app setup), no window.
/// </summary>
public static class TrayIconRenderer
{
    /// <param name="mono">The shapes' colour: <see cref="TrayGeometry.MonoOnLight"/> or <see cref="TrayGeometry.MonoOnDark"/>.</param>
    /// <param name="colourLayer">False in high contrast: the red parts are drawn in <paramref name="mono"/>.</param>
    public static RenderTargetBitmap Render(TrayGlyph glyph, int frame, int size, Color mono, bool colourLayer)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        IBrush shapes = new SolidColorBrush(mono);
        IBrush red = colourLayer ? new SolidColorBrush(Color.Parse(TrayGeometry.ColourLayer)) : shapes;
        var round = new Pen(shapes, TrayGeometry.Stroke, lineCap: PenLineCap.Round);
        var centre = new Point(TrayGeometry.Centre, TrayGeometry.Centre);
        var scale = size / TrayGeometry.Grid;

        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (glyph == TrayGlyph.Processing)
            {
                foreach (var (from, to) in TrayGeometry.Dashes(frame))
                {
                    context.DrawGeometry(null, round, Arc(from, to));
                }
            }
            else
            {
                context.DrawEllipse(null, new Pen(shapes, TrayGeometry.Stroke), centre, TrayGeometry.RingRadius, TrayGeometry.RingRadius);
            }

            switch (glyph)
            {
                case TrayGlyph.Idle or TrayGlyph.Processing:
                    context.DrawGeometry(null, round, Geometry.Parse(TrayGeometry.WavePath));
                    break;
                case TrayGlyph.Recording:
                    context.DrawEllipse(red, null, centre, TrayGeometry.DotRadius, TrayGeometry.DotRadius);
                    break;
                case TrayGlyph.Error:
                    context.DrawLine(
                        new Pen(red, TrayGeometry.ExclamationStroke, lineCap: PenLineCap.Round),
                        new Point(TrayGeometry.Centre, TrayGeometry.ExclamationTop),
                        new Point(TrayGeometry.Centre, TrayGeometry.ExclamationBottom));
                    context.DrawEllipse(
                        red, null, new Point(TrayGeometry.Centre, TrayGeometry.ExclamationDotY), TrayGeometry.ExclamationDotRadius, TrayGeometry.ExclamationDotRadius);
                    break;
                case TrayGlyph.Paused:
                    foreach (var left in (double[])[TrayGeometry.PauseBarLeft, TrayGeometry.PauseBarRight])
                    {
                        context.DrawRectangle(
                            shapes,
                            null,
                            new RoundedRect(new Rect(left, TrayGeometry.PauseBarTop, TrayGeometry.PauseBarWidth, TrayGeometry.PauseBarHeight), TrayGeometry.PauseBarRadius));
                    }

                    break;
            }
        }

        return bitmap;
    }

    /// <summary>A piece of the ring between two arc lengths, clockwise from three o'clock as an SVG circle runs.</summary>
    private static StreamGeometry Arc(double from, double to)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(PointAt(from), isFilled: false);
            path.ArcTo(PointAt(to), new Size(TrayGeometry.RingRadius, TrayGeometry.RingRadius), 0, to - from > TrayGeometry.RingLength / 2, SweepDirection.Clockwise);
            path.EndFigure(isClosed: false);
        }

        return geometry;
    }

    private static Point PointAt(double length)
    {
        var angle = length / TrayGeometry.RingRadius;
        return new Point(TrayGeometry.Centre + TrayGeometry.RingRadius * Math.Cos(angle), TrayGeometry.Centre + TrayGeometry.RingRadius * Math.Sin(angle));
    }
}
