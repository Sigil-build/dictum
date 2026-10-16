using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Platform.Linux;
using Dictum.Platform.Windows;

namespace Dictum.Host;

/// <summary>Picks the platform services for the current OS and session.</summary>
public static class PlatformFactory
{
    /// <param name="settings">Read at use time, so hot-reloaded settings apply without rebuilding the platform.</param>
    /// <param name="stateDirectory">Where the platform may keep state such as portal restore tokens.</param>
    public static PlatformServices Create(ILog log, Func<PlatformSettings> settings, string stateDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPlatform.Create(log, () => settings().TerminalApps);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxPlatform.Create(log, settings, stateDirectory);
        }

        return UnsupportedPlatform.Create(
            OperatingSystem.IsMacOS() ? "macOS" : "this OS",
            "Global hotkeys and paste are not supported on this OS yet; use 'dictum record --out clipboard'.");
    }

    /// <summary>Login start for this OS, without building the rest of the platform: the desktop app needs only this.</summary>
    public static IAutostart? CreateAutostart() =>
        OperatingSystem.IsWindows() ? new RunKeyAutostart()
        : OperatingSystem.IsLinux() ? new LinuxAutostart()
        : null;
}
