using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Core;

namespace Dictum.Plugins;

/// <summary>
/// The files a plugin's program is made of, which its trust record covers: <c>plugin.json</c>, the executable, and
/// every file under each entry of the manifest's <c>program</c>. A folder counts with every file beneath it, but no
/// folder named <c>__pycache__</c> is entered, nor a folder that is a link; a file that is a link counts as its
/// target. Each file is named by its path relative to the plugin's folder, written with <c>/</c>.
/// </summary>
internal sealed class PluginProgramFiles
{
    /// <summary>Python writes its caches here next to the modules it imports, so they change by themselves.</summary>
    private const string PythonCache = "__pycache__";

    /// <summary>Paths compare as the file system does: on Windows a path that differs only in case is the same file.</summary>
    private static readonly StringComparer Paths = StringComparer.FromComparison(PathComparison.Current);

    /// <summary>Hidden files too: a name that starts with a dot is one on Linux, and a program may load it.</summary>
    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>Each file by its relative path, and how to hash it; a hash that cannot be made throws why.</summary>
    private readonly Dictionary<string, Func<string>> _files;

    private PluginProgramFiles(Dictionary<string, Func<string>> files)
    {
        _files = files;
    }

    /// <summary>
    /// The program files as they are now. <c>plugin.json</c> is read once, so its hash and the entries it lists come
    /// from the same bytes. Listing never throws; a file or folder that cannot be read fails its hash instead.
    /// </summary>
    public static PluginProgramFiles List(string folder, string executable)
    {
        var files = new Dictionary<string, Func<string>>(Paths);
        var manifestPath = Path.Combine(folder, PluginProtocol.ManifestFile);
        byte[]? manifest = null;
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            files[PluginProtocol.ManifestFile] = () => Convert.ToHexStringLower(SHA256.HashData(bytes));
            manifest = bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files[PluginProtocol.ManifestFile] = () => throw new IOException($"{manifestPath} cannot be read: {ex.Message}", ex);
        }

        files.TryAdd(Relative(folder, executable), () => PluginTrustStore.HashFile(executable));
        foreach (var entry in Entries(manifest))
        {
            var path = Path.Combine(folder, entry);
            if (File.Exists(path))
            {
                files.TryAdd(Relative(folder, path), () => PluginTrustStore.HashFile(path));
            }
            else if (Directory.Exists(path))
            {
                Walk(folder, new DirectoryInfo(path), files);
            }
        }

        return new PluginProgramFiles(files);
    }

    /// <summary>Every file's hash, in ordinal order of the paths, for a new record.</summary>
    /// <exception cref="IOException">A file or a folder cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A file may not be read.</exception>
    public Dictionary<string, string> Hashes()
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in _files.Keys.Order(StringComparer.Ordinal))
        {
            hashes[relative] = _files[relative]();
        }

        return hashes;
    }

    /// <summary>
    /// The first path, in ordinal order, that is here but not in the record, in the record but not here, has another
    /// hash or cannot be read; null when the files are the ones recorded.
    /// </summary>
    public string? FirstDifference(IReadOnlyDictionary<string, string> recorded)
    {
        var expected = new Dictionary<string, string>(Paths);
        foreach (var (relative, hash) in recorded)
        {
            expected.TryAdd(relative, hash);
        }

        foreach (var relative in _files.Keys.Concat(expected.Keys).Distinct(Paths).Order(StringComparer.Ordinal))
        {
            if (!_files.TryGetValue(relative, out var hasher)
                || !expected.TryGetValue(relative, out var hash)
                || !string.Equals(TryHash(hasher), hash, StringComparison.Ordinal))
            {
                return relative;
            }
        }

        return null;
    }

    private static string? TryHash(Func<string> hasher)
    {
        try
        {
            return hasher();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The entries of <c>program</c> that name a path inside the folder, parsed as <see cref="PluginManifest.Load"/>
    /// parses the file; none when it is not JSON, and then <see cref="PluginManifest.Load"/> refuses it too. An entry
    /// the manifest's check refuses is skipped: such a manifest never loads, so its plugin never starts.
    /// </summary>
    private static IEnumerable<string> Entries(byte[]? manifest)
    {
        if (manifest is null)
        {
            return [];
        }

        try
        {
            return PluginManifest.Parse(manifest)?.Program?.Where(PluginManifest.IsProgramEntry) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Walk(string folder, DirectoryInfo directory, Dictionary<string, Func<string>> files)
    {
        try
        {
            // A link may lead anywhere, back up the tree included, so only real folders are entered.
            if (directory.Name == PythonCache || IsLink(directory))
            {
                return;
            }

            foreach (var file in directory.EnumerateFiles("*", Everything))
            {
                var path = file.FullName;
                files.TryAdd(Relative(folder, path), () => PluginTrustStore.HashFile(path));
            }

            foreach (var child in directory.EnumerateDirectories("*", Everything))
            {
                Walk(folder, child, files);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed: its files cannot be compared, so the folder itself counts as unreadable.
            files[Relative(folder, directory.FullName)] = () => throw new IOException($"{directory.FullName} cannot be listed: {ex.Message}", ex);
        }
    }

    /// <summary>A symbolic link or a junction; Windows marks other files, such as cloud placeholders, as reparse points too.</summary>
    internal static bool IsLink(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
}
