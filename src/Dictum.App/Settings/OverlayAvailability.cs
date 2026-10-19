using Dictum.App.Resources;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Settings;

public enum OverlayHost
{
    Windows,
    X11,
    XWayland,
    NoX,
    Unsupported,
}

/// <summary>
/// The overlay row of Settings → System. The overlay is a layered window on Windows and an X11 window elsewhere, so a
/// Wayland session needs XWayland; macOS has no overlay path.
/// </summary>
public static class OverlayAvailability
{
    public static OverlayHost ThisDesktop(Func<string, string?> env) =>
        OperatingSystem.IsWindows() ? OverlayHost.Windows
        : !OperatingSystem.IsLinux() ? OverlayHost.Unsupported
        : string.IsNullOrEmpty(env("DISPLAY")) ? OverlayHost.NoX
        : string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) ? OverlayHost.X11
        : OverlayHost.XWayland;

    public static SystemRow Row(OverlayHost host, bool enabled) => (host, enabled) switch
    {
        (_, false) => new SystemRow(Strings.SettingsOverlay, HealthLevel.Ok, Strings.SettingsOverlayRowOff, Strings.SettingsOverlayRowOffDetail),
        (OverlayHost.NoX, _) => new SystemRow(Strings.SettingsOverlay, HealthLevel.Error, Strings.SettingsOverlayRowNoX, Strings.SettingsOverlayRowNoXDetail),
        (OverlayHost.Unsupported, _) => new SystemRow(Strings.SettingsOverlay, HealthLevel.Warning, Strings.SettingsOverlayRowUnsupported),
        _ => new SystemRow(Strings.SettingsOverlay, HealthLevel.Ok, Strings.SettingsOverlayRowReady, Strings.SettingsOverlayRowReadyDetail),
    };
}
