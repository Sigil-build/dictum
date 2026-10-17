using Avalonia;
using Dictum.Core.Configuration;

namespace Dictum.App.Overlay;

/// <summary>A screen as the overlay sees it: physical pixels, the DPI scale and the OS's name for the output.</summary>
public sealed record ScreenArea(string? Name, PixelRect Bounds, PixelRect WorkingArea, double Scaling, bool IsPrimary);

/// <summary>Where the pointer is, as far as the desktop can tell: a point, an output name (KWin), or nothing.</summary>
public readonly record struct ScreenHint(PixelPoint? Point, string? Name)
{
    public static readonly ScreenHint None = default;
}

/// <summary>Spec 5.4, "Placement": the screen under the pointer, centred, 4 px above the bottom panel or below the top one.</summary>
public static class OverlayPlacement
{
    /// <summary>The panel height assumed where a working-area inset cannot be trusted, in overlay units.</summary>
    public const double FallbackPanel = 48;

    /// <summary>An inset larger than this share of the screen is another screen's (spike: _NET_WORKAREA is one rectangle).</summary>
    public const double MaxInsetShare = 0.15;

    public static ScreenArea? Choose(IReadOnlyList<ScreenArea> screens, ScreenHint hint)
    {
        if (hint.Name is { } name && screens.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return named;
        }

        if (hint.Point is { } point && screens.FirstOrDefault(s => s.Bounds.Contains(point)) is { } under)
        {
            return under;
        }

        return screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
    }

    /// <summary>The window's rectangle in physical pixels.</summary>
    public static PixelRect Place(ScreenArea screen, OverlaySize size, OverlayPosition position)
    {
        var zoom = OverlayTokens.SizeScale(size) * screen.Scaling;
        var width = (int)Math.Round(OverlayTokens.Width * zoom);
        var height = (int)Math.Round(OverlayTokens.Height * zoom);
        var gap = (int)Math.Round(OverlayTokens.MarginFromPanel * screen.Scaling);
        var area = Usable(screen);
        var x = area.X + (area.Width - width) / 2;
        var y = position == OverlayPosition.Top ? area.Y + gap : area.Bottom - height - gap;
        return new PixelRect(x, y, width, height);
    }

    /// <summary>The working area, with any inset larger than 15 % of the screen replaced by a usual panel.</summary>
    public static PixelRect Usable(ScreenArea screen)
    {
        var bounds = screen.Bounds;
        var work = screen.WorkingArea is { Width: > 0, Height: > 0 } area && bounds.Contains(area) ? area : bounds;
        var panel = (int)Math.Round(FallbackPanel * screen.Scaling);
        int Inset(int value, int side) => value > side * MaxInsetShare ? panel : value;
        var left = Inset(work.X - bounds.X, bounds.Width);
        var right = Inset(bounds.Right - work.Right, bounds.Width);
        var top = Inset(work.Y - bounds.Y, bounds.Height);
        var bottom = Inset(bounds.Bottom - work.Bottom, bounds.Height);
        return new PixelRect(bounds.X + left, bounds.Y + top, bounds.Width - left - right, bounds.Height - top - bottom);
    }
}
