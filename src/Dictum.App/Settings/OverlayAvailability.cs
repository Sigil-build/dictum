using Dictum.App.Resources;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux;

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
    /// <param name="env">The session's environment variables.</param>
    public static OverlayHost ThisDesktop(bool windows, bool linux, Func<string, string?> env) =>
        windows ? OverlayHost.Windows
        : !linux ? OverlayHost.Unsupported
        : string.IsNullOrEmpty(env(LinuxEnvironment.Display)) ? OverlayHost.NoX
        : string.IsNullOrEmpty(env(LinuxEnvironment.WaylandDisplay)) ? OverlayHost.X11
        : OverlayHost.XWayland;

    public static SystemRow Row(OverlayHost host, bool enabled) => (host, enabled) switch
    {
        (_, false) => new SystemRow(Strings.SettingsOverlay, HealthLevel.Ok, Strings.SettingsOverlayRowOff, Strings.SettingsOverlayRowOffDetail),
        (OverlayHost.NoX, _) => new SystemRow(Strings.SettingsOverlay, HealthLevel.Error, Strings.SettingsOverlayRowNoX, Strings.SettingsOverlayRowNoXDetail),
        (OverlayHost.Unsupported, _) => new SystemRow(Strings.SettingsOverlay, HealthLevel.Warning, Strings.SettingsOverlayRowUnsupported),
        _ => new SystemRow(Strings.SettingsOverlay, HealthLevel.Ok, Strings.SettingsOverlayRowReady, Strings.SettingsOverlayRowReadyDetail),
    };
}
