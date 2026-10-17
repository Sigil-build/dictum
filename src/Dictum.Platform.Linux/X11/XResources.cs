using System.Globalization;

namespace Dictum.Platform.Linux.X11;

public static class XResources
{
    /// <summary>Xft.dpi from an X resource database (a line such as <c>Xft.dpi: 120</c>); null when absent or not a number.</summary>
    public static double? Dpi(string? database)
    {
        foreach (var line in (database ?? "").Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith("Xft.dpi:", StringComparison.Ordinal))
            {
                return double.TryParse(entry["Xft.dpi:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) && dpi > 0
                    ? dpi
                    : null;
            }
        }

        return null;
    }
}
