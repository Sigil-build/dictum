using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux;
using Dictum.Platform.Windows;

namespace Dictum.App.Settings;

public enum TrayDesktop
{
    Windows,
    Kde,
    Other,
}

/// <summary>The hidden-tray-icon row of Settings → System. Windows gets a button to its taskbar settings; KDE gets the path.</summary>
public static class TrayVisibility
{
    /// <param name="available">The tray host took the icon.</param>
    /// <param name="hidden">The OS keeps the icon in an overflow area; null when unknown.</param>
    public static SystemRow Row(TrayDesktop desktop, bool available, bool? hidden, Action? openSettings)
    {
        if (!available)
        {
            return new SystemRow(Strings.SettingsTrayName, HealthLevel.Warning, Strings.SettingsTrayNoHost, Strings.StatusNoTray);
        }

        if (hidden != true)
        {
            return new SystemRow(Strings.SettingsTrayName, HealthLevel.Ok, hidden == false ? Strings.SettingsTrayShown : Strings.SettingsTrayInTray);
        }

        return desktop == TrayDesktop.Windows
            ? new SystemRow(Strings.SettingsTrayName, HealthLevel.Warning, Strings.SettingsTrayHidden, Strings.SettingsTrayHiddenWindows,
                openSettings is null ? null : Strings.SettingsTrayOpen, openSettings is null ? null : new RelayCommand(openSettings))
            : new SystemRow(Strings.SettingsTrayName, HealthLevel.Warning, Strings.SettingsTrayHidden, Strings.SettingsTrayHiddenKde);
    }

    /// <summary>Reads where this desktop keeps the setting; null when it cannot tell. Never throws.</summary>
    public static bool? Hidden(TrayDesktop desktop, string? executable, ILog log)
    {
        try
        {
            if (desktop == TrayDesktop.Windows && OperatingSystem.IsWindows() && executable is not null)
            {
                return new TrayIconVisibility().IsPromoted(executable) is { } promoted ? !promoted : null;
            }

            if (desktop == TrayDesktop.Kde)
            {
                var file = PlasmaTray.ConfigFile(Environment.GetEnvironmentVariable);
                return File.Exists(file) ? PlasmaTray.IsHidden(File.ReadAllText(file), PlasmaTray.ItemId) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Debug($"Tray visibility not read: {ex.Message}");
        }

        return null;
    }

    /// <summary>Opens Windows' taskbar settings, where the icon can be pinned.</summary>
    /// <param name="start">Starts the settings page; <see cref="Process.Start(ProcessStartInfo)"/> when left out.</param>
    public static void OpenTaskbarSettings(ILog log, Func<ProcessStartInfo, Process?>? start = null)
    {
        try
        {
            (start ?? Process.Start)(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Some Windows editions (Server, LTSC) have no ms-settings: handler; the button then does nothing.
            log.Warn($"Taskbar settings not opened: {ex.Message}");
        }
    }
}
