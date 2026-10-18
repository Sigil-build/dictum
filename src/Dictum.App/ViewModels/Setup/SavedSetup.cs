using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.App.ViewModels.Setup;

/// <summary>
/// The wizard's answers when step 7 started, kept in <c>&lt;data&gt;/setup.json</c> so a GUI restart in the middle of
/// the downloads returns to the walk (spec 8, step 7). A plain API key is never written: <see cref="KeyTyped"/> says
/// one was typed, and the resumed walk asks for it again.
/// </summary>
/// <param name="SpeechModel">The catalog id; null keeps the hand-placed file.</param>
/// <param name="KeyReference">An <c>env:</c> or <c>secret:</c> reference the user typed; never a plain key.</param>
/// <param name="Files">The catalog ids step 7 downloads.</param>
public sealed record SavedSetup(
    string Language,
    string? SpeechModel,
    bool UseServer,
    string Url,
    string Model,
    string? KeyReference,
    bool KeyTyped,
    IReadOnlyList<string> Words,
    bool StartAtLogin,
    IReadOnlyList<string> Files,
    int Version = 1);

/// <summary>Reads, writes and removes <c>setup.json</c>; a disk or format problem is logged, never thrown (4a I3).</summary>
public static class SavedSetupFile
{
    /// <summary>The kept walk, or null. A damaged file, or one from another version, is removed: the walk starts over.</summary>
    public static SavedSetup? Load(AppPaths paths, ILog log)
    {
        if (!File.Exists(paths.SetupFile))
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllText(paths.SetupFile), SetupJsonContext.Default.SavedSetup);
            if (saved is { Version: 1, Language: not null, Url: not null, Model: not null, Words: not null, Files: not null })
            {
                return saved;
            }

            log.Warn("setup.json is from another version or incomplete; the setup starts over.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            log.Warn($"setup.json could not be read; the setup starts over: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"setup.json could not be read: {ex.Message}");
            return null;
        }

        Delete(paths, log);
        return null;
    }

    public static void Save(AppPaths paths, SavedSetup saved, ILog log)
    {
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            var temp = paths.SetupFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, SetupJsonContext.Default.SavedSetup));
            File.Move(temp, paths.SetupFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"setup.json not written; a restart starts the setup over: {ex.Message}");
        }
    }

    public static void Delete(AppPaths paths, ILog log)
    {
        try
        {
            File.Delete(paths.SetupFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"setup.json not removed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SavedSetup))]
internal sealed partial class SetupJsonContext : JsonSerializerContext;
