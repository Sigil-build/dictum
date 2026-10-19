using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Tray;

namespace Dictum.App.Views;

/// <summary>What Dictum's windows share: Mica behind the content on Windows 11, and Dictum's mark as the window icon.</summary>
internal static class WindowChrome
{
    /// <summary>Mica needs Windows 11, whose builds start here.</summary>
    private const int Windows11Build = 22000;

    private const int IconSize = 32;

    public static void Apply(Window window)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11Build))
        {
            window.TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            window.Background = Brushes.Transparent;
        }

        // The tray's idle glyph, so the title bar and the task switcher show Dictum's mark.
        window.Icon = new WindowIcon(TrayIconRenderer.Render(TrayGlyph.Idle, frame: 0, IconSize, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true));
    }
}
