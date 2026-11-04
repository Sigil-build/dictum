using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.Plugins;

public enum PluginTrust
{
    /// <summary>The user confirmed this plugin, from this folder, and its program files are the ones they confirmed.</summary>
    Confirmed,

    /// <summary>No usable record, or the record is for another folder or program path.</summary>
    NotConfirmed,

    /// <summary>Confirmed before, but a program file changed, was added or is gone, or cannot be read.</summary>
    Changed,
}

/// <summary>What the trust check found, and for a plugin that changed, the first of its files that differs.</summary>
/// <param name="ChangedFile">Set when <see cref="PluginTrust.Changed"/>: its path relative to the plugin's folder, with <c>/</c>.</param>
public sealed record PluginTrustResult(PluginTrust Trust, string? ChangedFile = null);

/// <summary>
/// <c>plugin-trust.json</c>: which user-added plugins ran with the user's say-so, and the hash of each of their
/// program files as the user confirmed them (<see cref="PluginProgramFiles"/>). The file is read on every check, so a
/// daemon sees a confirmation made by the CLI without a reload.
/// </summary>
public sealed class PluginTrustStore(string path, ILog log)
{
    private const int FileVersion = 1;

    /// <summary>
    /// Confirmed while the record is for this folder and executable and every program file is as it was; a file
    /// added, gone, changed or unreadable is a change, and the first such path in ordinal order is named.
    /// </summary>
    public PluginTrustResult Check(string id, string folder, string executable)
    {
        if (!Read().Custom.TryGetValue(id, out var record)
            || !SamePath(record.Folder, folder)
            || !SamePath(record.Executable, executable))
        {
            return new PluginTrustResult(PluginTrust.NotConfirmed);
        }

        return PluginProgramFiles.List(folder, executable).FirstDifference(record.Files) is { } changed
            ? new PluginTrustResult(PluginTrust.Changed, changed)
            : new PluginTrustResult(PluginTrust.Confirmed);
    }

    /// <summary>Records the plugin as confirmed with its program files as they are now. Replaces a damaged file.</summary>
    /// <exception cref="PluginTrustException">A newer Dictum wrote the file, which this one leaves as it is.</exception>
    /// <exception cref="IOException">A program file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A program file may not be read.</exception>
    public void Confirm(string id, string folder, string executable)
    {
        var file = Read();
        if (file.Version > FileVersion)
        {
            throw new PluginTrustException(
                $"{Newer(file.Version)}, so this Dictum does not change it.",
                "Update Dictum, then confirm the plugin again.");
        }

        file.Custom[id] = new PluginTrustRecord(Path.GetFullPath(folder), Path.GetFullPath(executable), PluginProgramFiles.List(folder, executable).Hashes());
        Write(file);
    }

    /// <summary>Drops the plugin's record; a file that a newer Dictum wrote holds none for this one, so it is left as it is.</summary>
    public void Forget(string id)
    {
        var file = Read();
        if (file.Custom.Remove(id))
        {
            Write(file);
        }
    }

    /// <summary>The lowercase hex SHA-256 of the file.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private string Newer(int version) =>
        $"{path} was written by a newer Dictum (file version {version.ToString(CultureInfo.InvariantCulture)}; this one reads {FileVersion})";

    private static bool SamePath(string recorded, string actual)
    {
        try
        {
            return string.Equals(Normalize(recorded), Normalize(actual), PathComparison.Current);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// A record that parsed but cannot be used: a null record, a missing field, a file without its hash or a path with
    /// an invalid character. A record without <c>files</c> is one that only development builds wrote.
    /// </summary>
    private static bool IsUsable(PluginTrustRecord? record)
    {
        if (record is not { Folder: not null, Executable: not null, Files: not null } || record.Files.Values.Any(hash => hash is null))
        {
            return false;
        }

        try
        {
            _ = Normalize(record.Folder);
            _ = Normalize(record.Executable);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// An absent file is an empty record; a damaged one trusts nothing, and says so. So does one that a newer Dictum
    /// wrote, which may record more than this one checks; it keeps its version, so that a confirmation is refused.
    /// </summary>
    private PluginTrustFile Read()
    {
        if (!File.Exists(path))
        {
            return Empty();
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), PluginJsonContext.Default.PluginTrustFile);
            if (file is { Version: > FileVersion })
            {
                log.Warn($"{Newer(file.Version)}, so no plugin counts as confirmed.");
                return file with { Custom = new Dictionary<string, PluginTrustRecord>(StringComparer.Ordinal) };
            }

            if (file is not { Custom: not null })
            {
                return Empty();
            }

            var unusable = file.Custom.Where(r => !IsUsable(r.Value)).Select(r => r.Key).ToList();
            if (unusable.Count > 0)
            {
                // The other records stay; the unusable ones count as not confirmed and a Confirm drops them.
                log.Warn($"{path} holds unusable records ({string.Join(", ", unusable)}), so those plugins do not count as confirmed.");
                foreach (var id in unusable)
                {
                    file.Custom.Remove(id);
                }
            }

            return file;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"{path} cannot be read, so no plugin counts as confirmed: {ex.Message}");
            return Empty();
        }
    }

    private void Write(PluginTrustFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, PluginJsonContext.Default.PluginTrustFile));
    }

    private static PluginTrustFile Empty() => new(FileVersion, new Dictionary<string, PluginTrustRecord>(StringComparer.Ordinal));
}

/// <summary><c>plugin-trust.json</c> cannot take a confirmation, such as one that a newer Dictum wrote.</summary>
public sealed class PluginTrustException(string message, string? hint = null) : Exception(message), IHasHint
{
    public string? Hint { get; } = hint;
}

internal sealed record PluginTrustFile(int Version, Dictionary<string, PluginTrustRecord> Custom);

/// <param name="Files">The hash of each program file, by its path relative to <paramref name="Folder"/>, written with <c>/</c>.</param>
internal sealed record PluginTrustRecord(string Folder, string Executable, Dictionary<string, string> Files);
