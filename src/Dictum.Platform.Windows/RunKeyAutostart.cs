using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Dictum.Platform.Windows;

/// <summary>Starts <c>dictum daemon</c> at login via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
[SupportedOSPlatform("windows")]
public sealed class RunKeyAutostart : IAutostart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dictum";

    public string Location => $@"HKCU\{KeyPath}\{ValueName}";

    public bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public Task InstallAsync(string executable, CancellationToken ct)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ValueName, $"\"{executable}\" daemon");
        return Task.CompletedTask;
    }

    public Task UninstallAsync(CancellationToken ct)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        return Task.CompletedTask;
    }
}
