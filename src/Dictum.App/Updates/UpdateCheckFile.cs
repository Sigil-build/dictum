using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Updates;

/// <summary>What <c>update-check.json</c> keeps between runs of dictum-gui.</summary>
/// <remarks>Init-only, no constructor: the source generator then leaves a missing value at its default.</remarks>
public sealed record UpdateCheckState
{
    /// <summary>When a check last got an answer from GitHub; a failed check leaves it.</summary>
    public DateTimeOffset? LastChecked { get; init; }

    /// <summary>The newest release that check found, as <c>X.Y.Z</c>.</summary>
    public string? Latest { get; init; }

    /// <summary>That release's page on GitHub.</summary>
    public string? Page { get; init; }

    /// <summary>The last version the user was told about, so a version is announced once.</summary>
    public string? Notified { get; init; }
}

/// <summary>Reads and writes <c>&lt;data&gt;/update-check.json</c>; a disk or format problem is logged, never thrown.</summary>
public sealed class UpdateCheckFile(string path, ILog log)
{
    private const string Name = "update-check.json";

    /// <summary>The kept state; none when there is no file or it cannot be read.</summary>
    public UpdateCheckState Load()
    {
        if (!File.Exists(path))
        {
            return new UpdateCheckState();
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJsonContext.Default.UpdateCheckState) ?? new UpdateCheckState();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"{Name} could not be read; the next check starts over: {ex.Message}");
            return new UpdateCheckState();
        }
    }

    public void Save(UpdateCheckState state)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
            {
                Directory.CreateDirectory(folder);
            }

            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(state, UpdateJsonContext.Default.UpdateCheckState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"{Name} not written; a restart may announce the version again: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(UpdateCheckState))]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
