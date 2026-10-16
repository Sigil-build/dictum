using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Dictum.Platform.Windows;

/// <summary>What the tray icon needs to know about the taskbar.</summary>
[SupportedOSPlatform("windows")]
public static class TaskbarInfo
{
    private const int SmCxSmIcon = 49;

    /// <summary>The small-icon size at the system DPI: 16 px at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %.</summary>
    public static int SmallIconSize() => Math.Clamp(Win32.GetSystemMetricsForDpi(SmCxSmIcon, Win32.GetDpiForSystem()), 16, 64);

    /// <summary>The taskbar's mode, from "Choose your default Windows mode" (not the apps' mode).</summary>
    public static bool UsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return IsLight(key?.GetValue("SystemUsesLightTheme"));
    }

    public static bool IsLight(object? registryValue) => registryValue is int dword && dword != 0;
}
