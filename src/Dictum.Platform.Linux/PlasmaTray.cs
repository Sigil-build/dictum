namespace Dictum.Platform.Linux;

/// <summary>
/// Plasma's system tray settings. The tray is a containment of its own,
/// <c>plugin=org.kde.plasma.private.systemtray</c>, in <c>plasma-org.kde.plasma.desktop-appletsrc</c>. Its
/// <c>[General]</c> group lists <c>hiddenItems</c> by StatusNotifierItem id, unless <c>showAllItems=true</c>.
/// </summary>
public static class PlasmaTray
{
    /// <summary>Dictum's StatusNotifierItem id; Plasma keys per-item visibility on it, so it never changes.</summary>
    public const string ItemId = "dictum";

    private const string SystemTray = "org.kde.plasma.private.systemtray";

    public static string ConfigFile(Func<string, string?> env) =>
        Path.Combine(LinuxEnvironment.ConfigDirectory(env), "plasma-org.kde.plasma.desktop-appletsrc");

    /// <summary>True when a system tray hides <paramref name="itemId"/>; null when the file has no system tray.</summary>
    public static bool? IsHidden(string appletsrc, string itemId)
    {
        var groups = Parse(appletsrc);
        var trays = groups.Where(g => g.Value.TryGetValue("plugin", out var plugin) && plugin == SystemTray).Select(g => g.Key).ToList();
        if (trays.Count == 0)
        {
            return null;
        }

        foreach (var tray in trays)
        {
            if (!groups.TryGetValue(tray + "[General]", out var general)
                || general.TryGetValue("showAllItems", out var all) && all == "true")
            {
                continue;
            }

            if (general.TryGetValue("hiddenItems", out var hidden) && hidden.Split(',').Contains(itemId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>KConfig groups by their full header (<c>[Containments][8][General]</c>), each with its keys.</summary>
    private static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (!groups.TryGetValue(line, out current))
                {
                    current = [];
                    groups[line] = current;
                }

                continue;
            }

            var equals = line.IndexOf('=');
            if (current is not null && equals > 0 && !line.StartsWith('#'))
            {
                current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        return groups;
    }
}
