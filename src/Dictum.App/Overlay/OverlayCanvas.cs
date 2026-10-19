using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Dictum.App.Resources;

namespace Dictum.App.Overlay;

/// <summary>
/// Draws an <see cref="OverlayScene"/>: the waves and every state, or the reduced-motion level bar. Every stroke is a
/// dark shadow, a glow and a core over one <see cref="StreamGeometry"/>. Drawing happens in overlay units, scaled by
/// <see cref="Zoom"/>. The whole drawing fades as one layer through <see cref="LayerOpacity"/>.
/// </summary>
public sealed class OverlayCanvas : Control
{
    private const double W = OverlayTokens.Width;
    private const double Cx = OverlayTokens.Width / 2;
    private const double Cy = OverlayTokens.CenterY;
    private const double HaloAlpha = 0.4;

    /// <summary>How much wider than its stroke the shadow under the check and the warning icon is.</summary>
    private const double IconShadowGrowth = 2;

    // The warning icon around its centre: the triangle's apex and base, then the exclamation mark's bar and dot.
    private const double WarningApex = 7;
    private const double WarningHalfBase = 7;
    private const double WarningBase = 6;
    private const double WarningBarTop = 2.5;
    private const double WarningBarBottom = 1.5;
    private const double WarningDotY = 4;
    private const double WarningDotRadius = 0.9;

    /// <summary>Moves the chip's label down half a unit, where it looks centred.</summary>
    private const double ChipLabelDrop = 0.5;

    /// <summary>Formatted texts kept before the cache starts over; the overlay shows only a few.</summary>
    private const int MaxCachedTexts = 64;

    private static readonly (double X, double Y)[] HaloOffsets = [(-0.75, 1), (0.75, 1), (0, 0.25), (0, 1.75)];
    private static readonly Rect TrackRect = new(
        Cx - OverlayTokens.MeterWidth / 2, Cy - OverlayTokens.MeterHeight / 2, OverlayTokens.MeterWidth, OverlayTokens.MeterHeight);

    private readonly Dictionary<(string, double, FontWeight, double, uint), FormattedText> _texts = [];
    private OverlayInk? _ink;
    private OverlayScene _scene = OverlayScene.Hidden;
    private double _zoom = 1;
    private double _layerOpacity = 1;
    private FontFamily? _font;

    /// <summary>The size setting's factor (0.8 / 1 / 1.25); the window's DPI scale comes on top.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// The whole overlay's opacity (the setting times the appear fade), applied as one layer so that overlapping strokes
    /// keep their look. Not <c>Visual.Opacity</c>: Avalonia applies that to each drawing operation.
    /// </summary>
    public double LayerOpacity
    {
        get => _layerOpacity;
        set
        {
            _layerOpacity = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    public OverlayScene Scene
    {
        get => _scene;
        set
        {
            _scene = value;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(W * _zoom, OverlayTokens.Height * _zoom);

    public override void Render(DrawingContext context)
    {
        var scene = _scene;
        if (scene.Phase is OverlayPhase.Hidden || _layerOpacity <= 0)
        {
            return;
        }

        if (_ink?.Palette != scene.Palette)
        {
            _ink = new OverlayInk(scene.Palette);
        }

        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom)))
        {
            if (_layerOpacity < 1)
            {
                var mask = new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(_layerOpacity * byte.MaxValue), 0, 0, 0));
                using (context.PushOpacityMask(mask, new Rect(0, 0, W, OverlayTokens.Height)))
                {
                    Draw(context, scene, _ink);
                }
            }
            else
            {
                Draw(context, scene, _ink);
            }
        }
    }

    private void Draw(DrawingContext context, OverlayScene scene, OverlayInk ink)
    {
        if (scene.ReducedMotion)
        {
            DrawReduced(context, scene, ink);
        }
        else
        {
            DrawWaves(context, scene, ink);
        }
    }

    private void DrawWaves(DrawingContext context, OverlayScene scene, OverlayInk ink)
    {
        var since = scene.Since;
        var time = scene.Time;
        switch (scene.Phase)
        {
            case OverlayPhase.Listening:
            {
                Waves(context, scene, ink, 1);
                Chip(context, ink, scene.Chip, 1);
                break;
            }

            case OverlayPhase.Transcribing:
            {
                // The waves keep moving while they flatten and fade into the line.
                var merge = CubicBezier.Decelerate.Ease(since / OverlayTokens.TranscribingMerge);
                if (merge < 1)
                {
                    Waves(context, scene, ink, 1 - merge);
                }

                Stroke(context, ink, Ink.Waves, merge, 0, W, x => WaveShapes.TranscribingY(x, time));
                Caption(context, Strings.OverlayTranscribing, Cy + OverlayTokens.CaptionOffset, OverlayTokens.CaptionAlpha * merge);
                break;
            }

            case OverlayPhase.CleaningUp:
            {
                var ramp = CubicBezier.Decelerate.Ease(since / OverlayTokens.CleaningEnter);
                Stroke(context, ink, Ink.Cleaning, 1, 0, W, x => WaveShapes.CleaningY(x, time, ramp));
                Caption(context, Strings.OverlayCleaningUp, Cy + OverlayTokens.CaptionOffset, OverlayTokens.CaptionAlpha);
                break;
            }

            case OverlayPhase.Done:
            {
                var alpha = OverlayMotion.DoneAlpha(since);
                var half = OverlayMotion.DoneHalfWidth(since);
                Stroke(context, ink, Ink.Done, alpha, Cx - half, Cx + half, _ => Cy);
                Check(context, ink, Cx, Cy, OverlayMotion.DoneCheck(since), alpha);
                Chip(context, ink, scene.Chip, alpha);
                break;
            }

            case OverlayPhase.Cancelled:
            {
                var progress = OverlayMotion.CancelledProgress(since);
                var slide = OverlayTokens.CancelledSlide * progress;
                Stroke(context, ink, Ink.Cancelled, 1 - progress,
                    OverlayTokens.DoneInset - slide, Cx - OverlayTokens.CancelledGap - slide, _ => Cy);
                Stroke(context, ink, Ink.Cancelled, 1 - progress,
                    Cx + OverlayTokens.CancelledGap + slide, W - OverlayTokens.DoneInset + slide, _ => Cy);
                break;
            }

            case OverlayPhase.Error:
            {
                var alpha = OverlayMotion.ErrorAlpha(since);
                Stroke(context, ink, Ink.Error, alpha * OverlayMotion.ErrorFlash(since), 0, W, _ => Cy);
                if (since > OverlayTokens.ErrorTextDelay)
                {
                    ErrorLine(context, ink, scene.Message, alpha);
                }

                break;
            }
        }
    }

    private void DrawReduced(DrawingContext context, OverlayScene scene, OverlayInk ink)
    {
        var since = scene.Since;
        var caption = Cy + OverlayTokens.ReducedCaptionOffset;
        switch (scene.Phase)
        {
            case OverlayPhase.Listening:
            {
                Track(context, ink, 1);
                Fill(context, ink, Ink.Meter, scene.Quiet ? OverlayTokens.MeterQuietFill : scene.Level, OverlayTokens.MeterAlpha);
                Caption(context, Strings.OverlayListening, caption, OverlayTokens.CaptionAlpha);
                Chip(context, ink, scene.Chip, 1);
                break;
            }

            case OverlayPhase.Transcribing:
            case OverlayPhase.CleaningUp:
            {
                var transcribing = scene.Phase is OverlayPhase.Transcribing;
                Track(context, ink, 1);
                Fill(context, ink, Ink.Meter, transcribing ? OverlayTokens.MeterTranscribingFill : OverlayTokens.MeterCleaningFill, OverlayTokens.MeterStaticAlpha);
                Caption(context, transcribing ? Strings.OverlayTranscribing : Strings.OverlayCleaningUp, caption, OverlayTokens.CaptionAlpha);
                break;
            }

            case OverlayPhase.Done:
            {
                var alpha = OverlayMotion.DoneAlpha(since);
                Track(context, ink, alpha);
                Fill(context, ink, Ink.Meter, 1, alpha);
                Check(context, ink, Cx, Cy + OverlayTokens.CaptionOffset, 1, alpha);
                Chip(context, ink, scene.Chip, alpha);
                break;
            }

            case OverlayPhase.Cancelled:
            {
                var alpha = OverlayMotion.CancelledFade(since);
                Track(context, ink, alpha);
                Caption(context, Strings.OverlayCancelled, caption, OverlayTokens.CaptionAlpha * alpha);
                break;
            }

            case OverlayPhase.Error:
            {
                var alpha = OverlayMotion.ErrorAlpha(since);
                Track(context, ink, alpha);
                Fill(context, ink, Ink.Error, 1, alpha);
                ErrorLine(context, ink, scene.Message, alpha);
                break;
            }
        }
    }

    private static void Waves(DrawingContext context, OverlayScene scene, OverlayInk ink, double fade)
    {
        for (var i = 0; i < OverlayTokens.Waves.Count; i++)
        {
            var amplitude = scene.Quiet
                ? WaveShapes.QuietAmplitude(i, scene.Time)
                : WaveShapes.ListeningAmplitude(i, scene.Level, scene.BandMeans[i], scene.Time);
            var alpha = scene.Quiet ? WaveShapes.QuietAlpha(i) : OverlayTokens.Waves[i].Alpha;
            var index = i;
            Stroke(context, ink, Ink.Waves, alpha * fade, 0, W, x => WaveShapes.WaveY(index, x, amplitude * fade, scene.Time));
        }
    }

    /// <summary>A dark offset shadow, a glow and a core over the same path.</summary>
    private static void Stroke(DrawingContext context, OverlayInk ink, Ink kind, double alpha, double x0, double x1, Func<double, double> y)
    {
        if (alpha <= 0 || x1 - x0 < 1)
        {
            return;
        }

        var path = Path(x0, x1, y);
        using (context.PushTransform(Matrix.CreateTranslation(0, OverlayTokens.ShadowOffsetY)))
        {
            context.DrawGeometry(null, ink.Pen(Ink.Shadow, alpha * OverlayTokens.ShadowAlpha, OverlayTokens.ShadowWidth), path);
        }

        context.DrawGeometry(null, ink.Pen(kind, alpha * ink.GlowOpacity, OverlayTokens.GlowWidth), path);
        context.DrawGeometry(null, ink.Pen(kind, alpha, OverlayTokens.CoreWidth), path);
    }

    private static StreamGeometry Path(double x0, double x1, Func<double, double> y)
    {
        var geometry = new StreamGeometry();
        using var figure = geometry.Open();
        WaveShapes.Trace(x0, x1, y, (x, py, first) =>
        {
            if (first)
            {
                figure.BeginFigure(new Point(x, py), isFilled: false);
            }
            else
            {
                figure.LineTo(new Point(x, py));
            }
        });
        figure.EndFigure(isClosed: false);
        return geometry;
    }

    private static void Check(DrawingContext context, OverlayInk ink, double cx, double cy, double progress, double alpha)
    {
        if (progress <= 0 || alpha <= 0)
        {
            return;
        }

        var points = WaveShapes.CheckPath(progress);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(cx + points[0].X, cy + points[0].Y), isFilled: false);
            for (var i = 1; i < points.Count; i++)
            {
                figure.LineTo(new Point(cx + points[i].X, cy + points[i].Y));
            }

            figure.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, ink.Pen(Ink.Shadow, alpha * OverlayTokens.CheckShadowAlpha, OverlayTokens.CheckWidth + IconShadowGrowth), geometry);
        context.DrawGeometry(null, ink.Pen(Ink.Done, alpha, OverlayTokens.CheckWidth), geometry);
    }

    private void ErrorLine(DrawingContext context, OverlayInk ink, string? message, double alpha)
    {
        var x = Cx + OverlayTokens.IconOffsetX;
        var y = Cy + OverlayTokens.CaptionOffset;
        Warning(context, ink, x, y, alpha);
        var left = Cx + OverlayTokens.ErrorTextOffsetX;
        Text(context, OneLine(message), OverlayTokens.ErrorTextSize, FontWeight.Normal, left, y, alpha, centred: false, W - OverlayTokens.ChipRight - left);
    }

    /// <summary>Provider errors carry pretty-printed JSON; the overlay has one line for them.</summary>
    private static string OneLine(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void Warning(DrawingContext context, OverlayInk ink, double x, double y, double alpha)
    {
        if (alpha <= 0)
        {
            return;
        }

        var icon = new StreamGeometry();
        using (var figure = icon.Open())
        {
            figure.BeginFigure(new Point(x, y - WarningApex), isFilled: false);
            figure.LineTo(new Point(x + WarningHalfBase, y + WarningBase));
            figure.LineTo(new Point(x - WarningHalfBase, y + WarningBase));
            figure.EndFigure(isClosed: true);
            figure.BeginFigure(new Point(x, y - WarningBarTop), isFilled: false);
            figure.LineTo(new Point(x, y + WarningBarBottom));
            figure.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, ink.Pen(Ink.Shadow, alpha * OverlayTokens.IconShadowAlpha, OverlayTokens.IconStroke + IconShadowGrowth), icon);
        context.DrawGeometry(null, ink.Pen(Ink.ErrorIcon, alpha, OverlayTokens.IconStroke), icon);
        context.DrawEllipse(ink.Brush(Ink.ErrorIcon, alpha), null, new Point(x, y + WarningDotY), WarningDotRadius, WarningDotRadius);
    }

    private static void Track(DrawingContext context, OverlayInk ink, double alpha)
    {
        if (alpha <= 0)
        {
            return;
        }

        var radius = OverlayTokens.MeterRadius;
        context.DrawRectangle(ink.Brush(Ink.Shadow, alpha * OverlayTokens.MeterShadowAlpha), null,
            TrackRect.Translate(new Vector(0, OverlayTokens.ShadowOffsetY)), radius, radius, default);
        context.DrawRectangle(ink.Brush(Ink.Track, alpha), null, TrackRect, radius, radius, default);
    }

    private static void Fill(DrawingContext context, OverlayInk ink, Ink kind, double fraction, double alpha)
    {
        if (fraction <= 0 || alpha <= 0)
        {
            return;
        }

        var width = Math.Max(OverlayTokens.MeterMinFill, OverlayTokens.MeterWidth * Math.Min(1, fraction));
        context.DrawRectangle(ink.Brush(kind, alpha), null, TrackRect.WithWidth(width), OverlayTokens.MeterRadius, OverlayTokens.MeterRadius, default);
    }

    private void Chip(DrawingContext context, OverlayInk ink, string? text, double alpha)
    {
        if (text is null || alpha <= 0)
        {
            return;
        }

        var label = Format(text, OverlayTokens.ChipSize, FontWeight.SemiBold, double.PositiveInfinity, OverlayTokens.TextFill);
        var width = label.WidthIncludingTrailingWhitespace + 2 * OverlayTokens.ChipPadding;
        var left = W - OverlayTokens.ChipRight - width;
        using (context.PushOpacity(alpha * OverlayTokens.ChipAlpha))
        {
            context.DrawRectangle(ink.Brush(Ink.Chip, 1), null,
                new Rect(left, Cy - OverlayTokens.ChipHeight / 2, width, OverlayTokens.ChipHeight), OverlayTokens.ChipRadius, OverlayTokens.ChipRadius, default);
            context.DrawText(label, new Point(left + OverlayTokens.ChipPadding, Cy + ChipLabelDrop - label.Height / 2));
        }
    }

    private void Caption(DrawingContext context, string text, double y, double alpha) =>
        Text(context, text, OverlayTokens.CaptionSize, FontWeight.Normal, Cx, y, alpha, centred: true, double.PositiveInfinity);

    /// <summary>One line of text over its halo; <paramref name="y"/> is the line's middle.</summary>
    private void Text(DrawingContext context, string text, double size, FontWeight weight, double x, double y, double alpha, bool centred, double maxWidth)
    {
        if (alpha <= 0 || text.Length == 0)
        {
            return;
        }

        var fill = Format(text, size, weight, maxWidth, OverlayTokens.TextFill);
        var halo = Format(text, size, weight, maxWidth, OverlayTokens.TextHalo);
        var origin = new Point(centred ? x - fill.WidthIncludingTrailingWhitespace / 2 : x, y - fill.Height / 2);
        using (context.PushOpacity(alpha))
        {
            using (context.PushOpacity(HaloAlpha))
            {
                foreach (var (dx, dy) in HaloOffsets)
                {
                    context.DrawText(halo, new Point(origin.X + dx, origin.Y + dy));
                }
            }

            context.DrawText(fill, origin);
        }
    }

    private FormattedText Format(string text, double size, FontWeight weight, double maxWidth, uint colour)
    {
        var key = (text, size, weight, maxWidth, colour);
        if (_texts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_texts.Count >= MaxCachedTexts)
        {
            _texts.Clear();
        }

        _font ??= this.TryFindResource(App.ContentFontKey, out var found) && found is FontFamily family ? family : FontFamily.Default;
        var formatted = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(_font, FontStyle.Normal, weight), size,
            new SolidColorBrush(Color.FromUInt32(colour)))
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (!double.IsPositiveInfinity(maxWidth))
        {
            formatted.MaxTextWidth = maxWidth;
        }

        _texts[key] = formatted;
        return formatted;
    }
}
