using System.Globalization;

namespace Dictum.App.Settings;

/// <summary>Ids for new profiles, providers, processors and prompts.</summary>
internal static class UniqueIds
{
    /// <summary><paramref name="stem"/> when it is free, otherwise the first free "stem-2", "stem-3"…</summary>
    public static string For(string stem, Func<string, bool> taken)
    {
        if (!taken(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}");
            if (!taken(id))
            {
                return id;
            }
        }
    }
}
