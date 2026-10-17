using System.Globalization;
using Dictum.App.Resources;
using Dictum.App.ViewModels.Settings;
using Dictum.Core;
using Dictum.Setup;

namespace Dictum.App.Settings;

/// <summary>
/// What a Local Whisper card can pick:
/// <list type="bullet">
/// <item>the catalog's installed Whisper models, by title;</item>
/// <item>other <c>.bin</c> files in the models folder, by name;</item>
/// <item>the configured file even when it is missing, marked so.</item>
/// </list>
/// Downloading is the Models page (PR 4c).
/// </summary>
public static class WhisperModels
{
    public static IReadOnlyList<Option<string>> Choices(AppPaths paths, Catalog catalog, string? current)
    {
        var choices = new List<Option<string>>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries.Where(e => e.Kind == CatalogKind.Whisper && ModelFiles.IsInstalled(paths, e)))
        {
            choices.Add(new Option<string>(entry.File, entry.Title));
            listed.Add(entry.File);
        }

        // VAD files are not speech models.
        listed.UnionWith(catalog.Entries.Where(e => e.Kind != CatalogKind.Whisper).Select(e => e.File));
        if (Directory.Exists(paths.ModelsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(paths.ModelsDirectory, "*.bin").Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (listed.Add(file))
                {
                    choices.Add(new Option<string>(file, file));
                }
            }
        }

        if (current is not null && !choices.Any(c => string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(current);
            choices.Add(new Option<string>(current, IsPresent(paths, current)
                ? name
                : string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersModelMissing, name)));
        }

        return choices;
    }

    public static bool IsPresent(AppPaths paths, string model) => File.Exists(paths.ResolveModel(model));
}
