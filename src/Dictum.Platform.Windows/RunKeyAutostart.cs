using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Dictum.Platform.Windows;

/// <summary>
/// One value, <c>Dictum</c>, under HKCU\Software\Microsoft\Windows\CurrentVersion\Run: either <c>dictum-gui</c> or
/// <c>dictum daemon</c>. <paramref name="keyPath"/> is for tests, which must not touch the real Run key.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunKeyAutostart(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run") : IAutostart
{
    private const string ValueName = "Dictum";

    public LoginApp? Installed
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) is string command ? Parse(command) : null;
        }
    }

    public string Location(LoginApp app) => $@"HKCU\{keyPath}\{ValueName}";

    /// <summary>An entry ending in <c>daemon</c> is the daemon alone; entries from v0.1 and v0.2 read that way too.</summary>
    public static LoginApp Parse(string command) =>
        command.TrimEnd().EndsWith(" " + LoginItem.DaemonArgument, StringComparison.Ordinal) ? LoginApp.Daemon : LoginApp.DesktopApp;

    public Task InstallAsync(LoginItem item, CancellationToken ct)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(ValueName, item.CommandLine);
        return Task.CompletedTask;
    }

    public Task UninstallAsync(CancellationToken ct)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        return Task.CompletedTask;
    }
}
