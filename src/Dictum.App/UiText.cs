using System.Globalization;

namespace Dictum.App;

/// <summary>Fills a resource string's placeholders in the UI's culture.</summary>
internal static class UiText
{
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
