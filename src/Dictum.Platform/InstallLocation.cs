namespace Dictum.Platform;

/// <summary>
/// What starts Dictum from outside the running process: login items and the portal's desktop entry. Normally the running
/// files. Inside an AppImage it is the AppImage file, because the AppImage mounts under a new path on every run; its
/// AppRun starts the desktop app, or the CLI when the first argument is <see cref="AppImageCli"/> (phase 4 spec 5.1).
/// </summary>
public static class InstallLocation
{
    /// <summary>The first argument that makes the AppImage's AppRun start the CLI.</summary>
    public const string AppImageCli = "dictum";

    /// <summary>The AppImage this process runs from (its runtime sets <c>APPIMAGE</c>), or null.</summary>
    public static string? AppImage =>
        OperatingSystem.IsLinux()
            ? AppImageOf(Environment.GetEnvironmentVariable("APPIMAGE"), Environment.GetEnvironmentVariable("APPDIR"), Environment.ProcessPath)
            : null;

    /// <summary>
    /// <paramref name="appImage"/> when <paramref name="processPath"/> lies inside <paramref name="appDir"/>, the AppImage's
    /// mount; null otherwise. A terminal inside another AppImage (an editor) passes both variables on to whatever it starts,
    /// so the variable alone does not say Dictum runs from an AppImage (final review I1).
    /// </summary>
    public static string? AppImageOf(string? appImage, string? appDir, string? processPath) =>
        appImage is { Length: > 0 } && appDir is { Length: > 0 } && processPath is not null
        && processPath.StartsWith(appDir.TrimEnd('/') + "/", StringComparison.Ordinal)
            ? appImage
            : null;

    /// <summary>The login item for the desktop app <paramref name="gui"/>, or for the AppImage it runs from.</summary>
    public static LoginItem DesktopApp(string gui, string? appImage) => new(LoginApp.DesktopApp, appImage ?? gui);

    /// <summary>The item that runs <c>dictum daemon</c>: <paramref name="dictum"/>, or the AppImage with its CLI word.</summary>
    public static LoginItem Daemon(string dictum, string? appImage) =>
        appImage is null ? new(LoginApp.Daemon, dictum) : new(LoginApp.Daemon, appImage, AppImage: true);
}
