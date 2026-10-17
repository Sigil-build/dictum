using System.Runtime.Versioning;

namespace Dictum.Platform.Windows;

/// <summary>
/// The listening overlay's Win32 side (spec 5.4): a click-through, never-activated window that stays out of Alt+Tab;
/// where the cursor is; and whether Windows plays animations (spec 5.5).
/// </summary>
[SupportedOSPlatform("windows")]
public static class OverlayWindowStyle
{
    public const long Layered = 0x80000;
    public const long Transparent = 0x20;
    public const long NoActivate = 0x08000000;
    public const long ToolWindow = 0x80;
    public const long Required = Layered | Transparent | NoActivate | ToolWindow;

    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>
    /// Adds the extended styles. A layered window stays invisible until its alpha is set, so the alpha is set to opaque:
    /// the per-pixel transparency still comes from Avalonia's composition surface.
    /// </summary>
    public static void Apply(nint window)
    {
        var style = (long)Win32.GetWindowLongPtr(window, Win32.GwlExStyle);
        Win32.SetWindowLongPtr(window, Win32.GwlExStyle, (nint)(style | Required));
        Win32.SetLayeredWindowAttributes(window, 0, 255, Win32.LwaAlpha);
    }

    public static long ExtendedStyle(nint window) => Win32.GetWindowLongPtr(window, Win32.GwlExStyle);

    /// <summary>The cursor in physical screen pixels; null when Windows does not say (a locked desktop).</summary>
    public static (int X, int Y)? CursorPosition() => Win32.GetCursorPos(out var point) ? (point.X, point.Y) : null;

    /// <summary>False when "Animation effects" is off in Windows settings; the overlay then shows its level bar.</summary>
    public static bool AnimationsEnabled() =>
        !Win32.SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) || enabled != 0;
}
