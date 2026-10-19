using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Dictum.Platform.Windows;

/// <summary>
/// Whether Windows 11 shows an app's notification icon on the taskbar. Windows keeps one subkey per icon under
/// HKCU\Control Panel\NotifyIconSettings, with the app's <c>ExecutablePath</c>. <c>IsPromoted</c> = 1 puts the icon on
/// the taskbar; without it the icon sits in the overflow. Paths under Program Files and the system folders start with
/// the known folder's GUID. <paramref name="keyPath"/> is for tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconVisibility(string keyPath = @"Control Panel\NotifyIconSettings")
{
    private static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
    };

    /// <summary>"{GUID}\rest" with a known folder's GUID becomes that folder's path plus the rest; anything else is kept.</summary>
    public static string Expand(string path)
    {
        var close = path.IndexOf('}');
        return path.StartsWith('{') && close > 0 && path.Length > close + 2 && path[close + 1] == '\\'
            && KnownFolders.TryGetValue(path[..(close + 1)], out var folder)
            ? Path.Combine(Environment.GetFolderPath(folder), path[(close + 2)..])
            : path;
    }

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
            if (key?.GetValue("ExecutablePath") is not string path || !string.Equals(Expand(path), executable, StringComparison.OrdinalIgnoreCase))
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
