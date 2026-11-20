using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Dictum.Platform.Windows;

/// <summary>
/// The folder the Windows installer put Dictum in, which it records as <c>HKCU\Software\Dictum\InstallFolder</c> (from
/// 0.7.0). The updater compares it with the running program's folder to tell an MSI install from the zip.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MsiInstallFolder
{
    public const string KeyPath = @"Software\Dictum";

    public const string ValueName = "InstallFolder";

    /// <summary>The recorded folder, as the MSI wrote it (with a trailing backslash); null when there is none or it cannot be read.</summary>
    public static string? Read() => Read(KeyPath);

    /// <summary>Tests read a throwaway key instead of the real one.</summary>
    internal static string? Read(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) as string is { Length: > 0 } folder ? folder : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
