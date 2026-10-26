using Dictum.Core;
using Dictum.Core.Configuration;

namespace Dictum.Setup;

/// <summary>Which profiles a downloaded model serves, so a model in use is never deleted from under them.</summary>
public static class ModelUse
{
    /// <summary>
    /// The ids of the profiles whose local Whisper provider runs <paramref name="entry"/>'s file, as its speech model or,
    /// with voice detection on, as its VAD model; none without a config.
    /// </summary>
    public static IReadOnlyList<string> ProfilesUsing(DictumConfig? config, AppPaths paths, CatalogEntry entry)
    {
        if (config is null)
        {
            return [];
        }

        var file = Path.GetFullPath(paths.ResolveModel(entry.File));
        bool Runs(string model) => string.Equals(Path.GetFullPath(paths.ResolveModel(model)), file, PathComparison.Current);

        return
        [
            .. config.Profiles
                .Where(p => config.Stt.GetValueOrDefault(p.Stt) is WhisperLocalSttConfig local && (Runs(local.Model) || (local.Vad && Runs(local.VadModel))))
                .Select(p => p.Id),
        ];
    }
}
