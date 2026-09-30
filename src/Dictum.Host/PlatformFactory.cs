using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Platform.Linux;
using Dictum.Platform.Windows;

namespace Dictum.Host;

/// <summary>Picks the platform services for the current OS and session.</summary>
public static class PlatformFactory
{
    /// <param name="settings">Read at use time, so hot-reloaded settings apply without rebuilding the platform.</param>
    public static PlatformServices Create(ILog log, Func<PlatformSettings> settings)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPlatform.Create(log, () => settings().TerminalApps);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxPlatform.Create(log, settings);
        }

        return UnsupportedPlatform.Create(
            OperatingSystem.IsMacOS() ? "macOS" : "this OS",
            "Global hotkeys and paste are not supported on this OS yet; use 'dictum record --out clipboard'.");
    }
}
