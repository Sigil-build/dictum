using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.ViewModels.Setup;

/// <summary>
/// The wizard's answers when step 7 started, kept in <c>&lt;data&gt;/setup.json</c> so a GUI restart in the middle of
/// the downloads returns to the walk. A plain API key is never written: <see cref="KeyTyped"/> says one was typed, and
/// the resumed walk asks for it again.
/// </summary>
/// <param name="SpeechModel">The catalog id; null keeps the hand-placed file.</param>
/// <param name="KeyReference">A typed <c>env:</c> or <c>secret:</c> reference; never a plain key.</param>
/// <param name="Files">The catalog ids step 7 downloads.</param>
/// <param name="BasedOn">The <see cref="SavedSetupFile.Fingerprint"/> of the config the walk was kept over; a walk that is
/// not downloading resumes only over the same config.</param>
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
    string? BasedOn = null,
    int Version = SavedSetupFile.CurrentVersion);

/// <summary>Reads, writes and removes <c>setup.json</c>; a disk or format problem is logged, never thrown.</summary>
public static class SavedSetupFile
{
    /// <summary>The shape of <c>setup.json</c>; a file of another version is dropped and the walk starts over.</summary>
    public const int CurrentVersion = 1;

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
            if (saved is { Version: CurrentVersion, Language: not null, Url: not null, Model: not null, Words: not null, Files: not null })
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

    /// <summary>A fingerprint of a config, or of none: a kept walk records the config it was made on.</summary>
    public static string Fingerprint(DictumConfig? config) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config is null ? "" : ConfigStore.Serialize(config))));

    public static void Save(AppPaths paths, SavedSetup saved, ILog log)
    {
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            AtomicFile.WriteAllText(paths.SetupFile, JsonSerializer.Serialize(saved, SetupJsonContext.Default.SavedSetup));
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
