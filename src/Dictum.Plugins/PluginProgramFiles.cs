using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Core;

namespace Dictum.Plugins;

/// <summary>
/// The files a plugin's program is made of, which its trust record covers: <c>plugin.json</c>, the executable, and
/// every file under each entry of the manifest's <c>program</c>, the whole folder for <c>.</c>. A folder counts with
/// every file beneath it, but no folder named <c>__pycache__</c> is entered, nor a folder that is a link: such a link
/// counts as an entry of its own, by where it points, so that one added, removed or pointed elsewhere is a change. So
/// does an entry that is a link or has one on the way. Any other file that is a link, a virtual environment's
/// interpreter say, counts as its target. A named pipe or a device cannot be read to an end, so it counts as a file that
/// cannot be read. Each is named by its path relative to the plugin's folder, written with <c>/</c>.
/// </summary>
internal sealed class PluginProgramFiles
{
    /// <summary>Python writes its caches here next to the modules it imports, so they change by themselves.</summary>
    private const string PythonCache = "__pycache__";

    /// <summary>What a linked folder records in place of a hash: this, then its target as the system reports it.</summary>
    private const string LinkPrefix = "link:";

    /// <summary>Paths compare as the file system does: on Windows a path that differs only in case is the same file.</summary>
    private static readonly StringComparer Paths = StringComparer.FromComparison(PathComparison.Current);

    /// <summary>Hidden files too: a name that starts with a dot is one on Linux, and a program may load it.</summary>
    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>How long a file that may be no plain file has to open; a plain one opens long before this, even on a busy disk.</summary>
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(10);

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
            var bytes = ReadAllBytes(manifestPath);
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
            try
            {
                if (LinkOnTheWay(folder, entry) is { } link)
                {
                    // The manifest refuses this as it loads, but a restart checks the files without loading it. Counted by
                    // the link, so that one made since is a change even when it leads to a copy of the same bytes: whatever
                    // takes the copy's place later would run unchecked.
                    // Over the executable's own row, if this is the executable: listing it says it may not be a link.
                    var target = LinkPrefix + link.LinkTarget;
                    files[Relative(folder, path)] = () => target;
                }
                else if (File.Exists(path))
                {
                    files.TryAdd(Relative(folder, path), () => PluginTrustStore.HashFile(path));
                }
                else if (Directory.Exists(path))
                {
                    // The plugin's own folder is entered even when it is a link: that is where the user keeps the plugin.
                    Walk(folder, new DirectoryInfo(path), files, isFolder: entry.Length == 0);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files[Relative(folder, path)] = () => throw new IOException($"{path} cannot be read: {ex.Message}", ex);
            }
        }

        return new PluginProgramFiles(files);
    }

    /// <summary>
    /// The link that the entry is, or that a folder on the way to it is; null when there is none. The plugin's folder itself
    /// may be a link: it is where the user keeps the plugin.
    /// </summary>
    /// <param name="entry">As <see cref="PluginManifest.ProgramPath"/> spells it; the empty path, the whole folder, has no way to go.</param>
    /// <exception cref="IOException">A path on the way cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A path on the way may not be read.</exception>
    internal static FileSystemInfo? LinkOnTheWay(string folder, string entry)
    {
        if (entry.Length == 0)
        {
            return null;
        }

        var path = folder;
        foreach (var segment in entry.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            path = Path.Combine(path, segment);
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (IsLink(info))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>The file's bytes, once it is known to be a plain file (<see cref="ThrowUnlessPlainFile"/>).</summary>
    /// <exception cref="IOException">The file cannot be read, or is no plain file.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    internal static byte[] ReadAllBytes(string path)
    {
        ThrowUnlessPlainFile(path);
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Refuses what is no plain file, so that reading it can end and an empty file cannot be swapped for it. A named pipe
    /// opens only once something writes to it, which may be never, and what it says then is not what was confirmed; a
    /// device or a file in /proc may have bytes without end. All of them report no size, and a link's own size says nothing
    /// of its target, so only those are opened here: on a thread of their own, which a pipe may hold until Dictum ends,
    /// since .NET has no way to ask what a file is or to open one without waiting.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read, or is no plain file.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    internal static void ThrowUnlessPlainFile(string path)
    {
        var file = new FileInfo(path);
        if (!IsLink(file) && file.Length > 0)
        {
            return;
        }

        Exception? failure = null;
        var opening = new Thread(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (!stream.CanSeek || (stream.Length == 0 && stream.ReadByte() >= 0))
                {
                    failure = new IOException($"{path} is a named pipe or a device, not a file.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Plugin file check",
        };
        opening.Start();
        if (!opening.Join(OpenWait))
        {
            throw new IOException($"{path} did not open within {OpenWait.TotalSeconds:0} seconds, as a named pipe with no writer does not.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
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
    /// The entries of <c>program</c> that name a path inside the folder, as <see cref="PluginManifest.ProgramPath"/>
    /// spells them, the whole folder as the empty path; parsed as <see cref="PluginManifest.Load"/> parses the file;
    /// none when it is not JSON, and then <see cref="PluginManifest.Load"/> refuses it too. An entry the manifest's check
    /// refuses is skipped: such a manifest never loads, so its plugin never starts.
    /// </summary>
    private static IEnumerable<string> Entries(byte[]? manifest)
    {
        if (manifest is null)
        {
            return [];
        }

        try
        {
            return PluginManifest.Parse(manifest)?.Program?.Select(PluginManifest.ProgramPath).OfType<string>() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <param name="isFolder">The plugin's own folder, which is entered whatever it is.</param>
    private static void Walk(string folder, DirectoryInfo directory, Dictionary<string, Func<string>> files, bool isFolder = false)
    {
        try
        {
            if (!isFolder && directory.Name == PythonCache)
            {
                return;
            }

            // A link may lead anywhere, back up the tree included, so only real folders are entered; where it points is
            // recorded instead, as it is when the record is made, so that a link added, removed or retargeted is a change.
            if (!isFolder && IsLink(directory))
            {
                var target = LinkPrefix + directory.LinkTarget;
                files.TryAdd(Relative(folder, directory.FullName), () => target);
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
