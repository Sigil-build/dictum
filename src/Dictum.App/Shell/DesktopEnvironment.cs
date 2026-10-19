using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dictum.App.Settings;
using Dictum.Platform.Linux;

namespace Dictum.App.Shell;

/// <summary>
/// The desktop dictum-gui runs on, read once at start: the OS, the Linux session, where the tray icon lives and how the
/// overlay can be drawn. The session and the overlay keep their own rules: the overlay needs an X display whatever the
/// session says it is.
/// </summary>
public sealed record DesktopEnvironment
{
    private DesktopEnvironment(bool isWindows, bool isLinux, SessionInfo? session, OverlayHost overlay)
    {
        IsWindows = isWindows;
        IsLinux = isLinux;
        Session = session;
        Overlay = overlay;
        Tray = isWindows ? TrayDesktop.Windows : session?.Desktop == Desktop.Kde ? TrayDesktop.Kde : TrayDesktop.Other;
    }

    [SupportedOSPlatformGuard("windows")]
    public bool IsWindows { get; }

    [SupportedOSPlatformGuard("linux")]
    public bool IsLinux { get; }

    /// <summary>The graphical session on Linux; null elsewhere.</summary>
    public SessionInfo? Session { get; }

    public TrayDesktop Tray { get; }

    public OverlayHost Overlay { get; }

    /// <summary>The desktop as the System page names it: the session on Linux, the OS elsewhere.</summary>
    public string Name => Session?.ToString() ?? RuntimeInformation.OSDescription;

    public static DesktopEnvironment Detect()
    {
        var windows = OperatingSystem.IsWindows();
        var linux = OperatingSystem.IsLinux();
        return new(windows, linux, linux ? SessionInfo.Detect() : null, OverlayAvailability.ThisDesktop(windows, linux, Environment.GetEnvironmentVariable));
    }
}
