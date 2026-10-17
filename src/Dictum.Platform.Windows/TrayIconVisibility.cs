using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Dictum.Platform.Windows;

/// <summary>
/// Whether Windows 11 shows an app's notification icon on the taskbar (spec 6). Windows keeps one subkey per icon
/// under HKCU\Control Panel\NotifyIconSettings, with the app's <c>ExecutablePath</c>. <c>IsPromoted</c> = 1 puts the
/// icon on the taskbar; without it the icon sits in the overflow. <paramref name="keyPath"/> is for tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconVisibility(string keyPath = @"Control Panel\NotifyIconSettings")
{
    /// <summary>Null when Windows has no entry for <paramref name="executable"/>: Windows 10, or the icon never showed.</summary>
    public bool? IsPromoted(string executable)
    {
        using var root = Registry.CurrentUser.OpenSubKey(keyPath);
        if (root is null)
        {
            return null;
        }

        bool? found = null;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("ExecutablePath") is not string path || !string.Equals(path, executable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key.GetValue("IsPromoted") is int promoted && promoted != 0)
            {
                return true;
            }

            found = false;
        }

        return found;
    }
}
