using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.Plugins;

public enum PluginTrust
{
    /// <summary>The user confirmed this plugin, from this folder, and its program is the one they confirmed.</summary>
    Confirmed,

    /// <summary>No record, or the record is for another folder or program path.</summary>
    NotConfirmed,

    /// <summary>Confirmed before, but the program is no longer the same file.</summary>
    Changed,
}

/// <summary>
/// <c>plugin-trust.json</c>: which user-added plugins ran with the user's say-so, and the hash of the program they
/// confirmed. The file is read on every check, so a daemon sees a confirmation made by the CLI without a reload.
/// </summary>
public sealed class PluginTrustStore(string path, ILog log)
{
    private const int FileVersion = 1;

    public PluginTrust Check(string id, string folder, string executable)
    {
        if (!Read().Custom.TryGetValue(id, out var record)
            || !SamePath(record.Folder, folder)
            || !SamePath(record.Executable, executable))
        {
            return PluginTrust.NotConfirmed;
        }

        return Hashes(executable, record.Sha256) ? PluginTrust.Confirmed : PluginTrust.Changed;
    }

    /// <summary>Records the plugin as confirmed with the program's hash as it is now. Replaces a damaged file.</summary>
    public void Confirm(string id, string folder, string executable)
    {
        var file = Read();
        file.Custom[id] = new PluginTrustRecord(Path.GetFullPath(folder), Path.GetFullPath(executable), HashFile(executable));
        Write(file);
    }

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

    private static bool Hashes(string executable, string expected)
    {
        try
        {
            return string.Equals(HashFile(executable), expected, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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

    /// <summary>A record that parsed but cannot be used: a null record, a missing field or a path with an invalid character.</summary>
    private static bool IsUsable(PluginTrustRecord? record)
    {
        if (record is not { Folder: not null, Executable: not null, Sha256: not null })
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

    /// <summary>An absent file is an empty record; a damaged one trusts nothing, and says so.</summary>
    private PluginTrustFile Read()
    {
        if (!File.Exists(path))
        {
            return Empty();
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), PluginJsonContext.Default.PluginTrustFile);
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

internal sealed record PluginTrustFile(int Version, Dictionary<string, PluginTrustRecord> Custom);

internal sealed record PluginTrustRecord(string Folder, string Executable, string Sha256);
