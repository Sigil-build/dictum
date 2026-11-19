using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.Updates;

/// <summary>An update handed to its runner, written just before: the next start of Dictum judges it.</summary>
/// <remarks>Init-only, no constructor, as <c>UpdateCheckState</c>: the source generator then leaves a missing value at its default.</remarks>
public sealed record PendingUpdate
{
    public required string Version { get; init; }

    [JsonConverter(typeof(InstallKindConverter))]
    public required InstallKind Kind { get; init; }

    /// <summary>The installer's log, which About Dictum opens when the update did not finish.</summary>
    public required string Log { get; init; }

    public DateTimeOffset Started { get; init; }
}

/// <summary>How a started update ended, as the next start sees it.</summary>
public enum UpdateOutcome
{
    /// <summary>No update was started.</summary>
    None,

    /// <summary>This version is the one the update installed, or newer.</summary>
    Updated,

    /// <summary>An older version runs: the installer failed or was cancelled, and the old version came back.</summary>
    DidNotFinish,

    /// <summary>Windows Installer replaced files in use; they take effect when Windows restarts (msiexec 3010).</summary>
    NeedsRestart,
}

/// <param name="Version">The version the update was to install.</param>
/// <param name="Log">The installer's log.</param>
public sealed record UpdateReport(UpdateOutcome Outcome, string? Version, string? Log);

/// <summary><c>&lt;data&gt;/updates/pending.json</c>; a disk or format problem is logged, never thrown, except by <see cref="Save"/>.</summary>
public sealed class PendingUpdateFile(string updatesDirectory, ILog log)
{
    public const string Name = "pending.json";

    /// <summary>The runner writes the installer's exit code here, in <c>&lt;updates&gt;/&lt;V&gt;/</c>.</summary>
    public const string ExitCodeFile = "exit-code";

    /// <summary>msiexec's "done, but restart Windows to finish".</summary>
    public const int RestartRequired = 3010;

    private string Path => System.IO.Path.Combine(updatesDirectory, Name);

    /// <summary>Throws when it cannot be written: an update must not start without its record.</summary>
    public void Save(PendingUpdate pending)
    {
        Directory.CreateDirectory(updatesDirectory);
        AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(pending, UpdatesJsonContext.Default.PendingUpdate));
    }

    /// <summary>Judges the update the record names against the running <paramref name="current"/> version, and deletes the record.</summary>
    public UpdateReport Take(ReleaseVersion current)
    {
        if (!File.Exists(Path))
        {
            return new UpdateReport(UpdateOutcome.None, null, null);
        }

        PendingUpdate? pending = null;
        try
        {
            pending = JsonSerializer.Deserialize(File.ReadAllText(Path), UpdatesJsonContext.Default.PendingUpdate);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"{Name} could not be read, so the last update is not reported: {ex.Message}");
        }

        Delete();
        if (pending is null || ReleaseVersion.Parse(pending.Version) is not { } target)
        {
            return new UpdateReport(UpdateOutcome.None, null, null);
        }

        var outcome = current >= target ? UpdateOutcome.Updated
            : ExitCode(pending.Version) == RestartRequired ? UpdateOutcome.NeedsRestart
            : UpdateOutcome.DidNotFinish;
        return new UpdateReport(outcome, pending.Version, pending.Log);
    }

    private int? ExitCode(string version)
    {
        try
        {
            var text = File.ReadAllText(System.IO.Path.Combine(updatesDirectory, version, ExitCodeFile)).Trim();
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Delete()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"{Name} could not be deleted, so the last update may be reported again: {ex.Message}");
        }
    }
}

/// <summary>Writes <see cref="InstallKind"/> as <c>msi</c>, <c>appImage</c>, <c>tarball</c>.</summary>
internal sealed class InstallKindConverter() : JsonStringEnumConverter<InstallKind>(JsonNamingPolicy.CamelCase);
