using Avalonia.Platform;

namespace Dictum.App.Tray;

/// <summary>Whether the tray sits on a light panel, which decides the icon's mono colour.</summary>
public static class TrayPanel
{
    /// <summary>
    /// A contrast theme paints the taskbar in its own colours and leaves the Windows mode as it was, so in high contrast
    /// the theme decides (Avalonia reports Light only for the light contrast themes, research §4); otherwise the
    /// taskbar's own mode.
    /// </summary>
    public static bool IsLight(PlatformColorValues? colours, Func<bool> taskbarIsLight) =>
        colours is { ContrastPreference: ColorContrastPreference.High }
            ? colours.ThemeVariant == PlatformThemeVariant.Light
            : taskbarIsLight();
}
