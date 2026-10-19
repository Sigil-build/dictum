using System.Globalization;
using Dictum.App.Resources;

namespace Dictum.App.Tray;

public static class TrayLabels
{
    /// <summary>Menus on both platforms read "_" as an access-key marker; "__" is a literal underscore.</summary>
    public static string Escape(string text) => text.Replace("_", "__", StringComparison.Ordinal);

    /// <summary>The item's text with its languages, escaped. Plasma shows the languages in a column instead.</summary>
    public static string ForItem(TrayMenuItem item) => Escape(item.Detail is { } detail
        ? string.Format(CultureInfo.CurrentCulture, Strings.MenuProfileWithLanguages, item.Label, detail)
        : item.Label);
}
