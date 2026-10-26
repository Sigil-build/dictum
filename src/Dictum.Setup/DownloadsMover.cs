using Dictum.Core;

namespace Dictum.Setup;

/// <summary>What moving the models to another downloads folder takes.</summary>
/// <param name="From">The current models directory.</param>
/// <param name="To">The models directory under the new downloads folder.</param>
/// <param name="Files">Paths relative to <paramref name="From"/>, every file under it (subfolders and <c>*.partial</c> included).</param>
/// <param name="Bytes">The size of <paramref name="Files"/> together.</param>
public sealed record DownloadsMove(string From, string To, IReadOnlyList<string> Files, long Bytes);

/// <summary>Moves the models to a new downloads folder: copy everything, let the caller switch the config, then delete the old files.</summary>
/// <remarks>
/// It always copies, even on one volume: a rename cannot be told apart from a copy across volumes without native calls on
/// Linux, and a copy keeps every old file in place until the config names the new folder, so a failure at any point
/// leaves Dictum with a complete models folder.
/// </remarks>
public static class DownloadsMover
{
    private const int BlockBytes = 1024 * 1024;

    /// <summary>Written under this suffix and renamed when complete, so a half-copied file never looks like a model.</summary>
    private const string MovingSuffix = ".moving";

    /// <exception cref="ArgumentException">The new models folder is the current one, or one lies inside the other.</exception>
    public static DownloadsMove Plan(AppPaths current, string newDownloadsDirectory)
    {
        var from = Path.GetFullPath(current.ModelsDirectory);
        var to = Path.GetFullPath(Path.Combine(newDownloadsDirectory, "models"));
        if (string.Equals(Path.TrimEndingDirectorySeparator(from), Path.TrimEndingDirectorySeparator(to), PathComparison.Current))
        {
            throw new ArgumentException($"{newDownloadsDirectory} is already the downloads folder.");
        }

        if (IsInside(to, from) || IsInside(from, to))
        {
            throw new ArgumentException($"The models folder {to} and the current one, {from}, lie inside one another; choose another folder.");
        }

        if (!Directory.Exists(from))
        {
            return new DownloadsMove(from, to, [], 0);
        }

        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList();
        return new DownloadsMove(from, to, [.. files.Select(f => Path.GetRelativePath(from, f))], files.Sum(f => new FileInfo(f).Length));
    }

    /// <summary>Copies every planned file to <see cref="DownloadsMove.To"/>, reporting the bytes copied so far; never touches <see cref="DownloadsMove.From"/>.</summary>
    /// <remarks>
    /// A file of the same size already at the target is kept as it is. On a failure or cancellation every file this call
    /// wrote, and every folder it created, is removed again before the exception goes on.
    /// </remarks>
    /// <exception cref="DownloadException">The target's drive is not there, or has less free space than the files need.</exception>
    public static async Task CopyAsync(DownloadsMove move, long freeBytesAtTarget, IProgress<long>? copiedBytes, CancellationToken ct)
    {
        if (move.Files.Count == 0)
        {
            return;
        }

        // A drive unplugged since the folder was chosen; its free space reads as unknown, which counts as plenty.
        if (Path.GetPathRoot(move.To) is not { Length: > 0 } root || !Directory.Exists(root))
        {
            throw new DownloadException(DownloadsFolder.MissingError(Path.GetDirectoryName(move.To) ?? move.To));
        }

        var needed = SetupRunner.NeededFor(move.Bytes);
        if (freeBytesAtTarget < needed)
        {
            throw new DownloadException(
                $"Not enough disk space in {move.To}: {SetupRunner.Format(needed)} needed, {SetupRunner.Format(freeBytesAtTarget)} free.");
        }

        var written = new List<string>();
        var createdFolders = new List<string>();
        var copied = 0L;
        try
        {
            foreach (var relative in move.Files)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(move.From, relative);
                var target = Path.Combine(move.To, relative);
                var length = new FileInfo(source).Length;
                if (File.Exists(target) && new FileInfo(target).Length == length)
                {
                    copied += length;
                    copiedBytes?.Report(copied);
                    continue;
                }

                CreateFolder(Path.GetDirectoryName(target)!, createdFolders);
                written.Add(target + MovingSuffix);
                copied = await CopyFileAsync(source, target + MovingSuffix, copied, copiedBytes, ct).ConfigureAwait(false);
                File.Move(target + MovingSuffix, target, overwrite: true);
                written.Add(target);
            }
        }
        catch
        {
            foreach (var file in written)
            {
                TryDelete(file);
            }

            foreach (var folder in Enumerable.Reverse(createdFolders))
            {
                TryDeleteEmptyFolder(folder);
            }

            throw;
        }
    }

    /// <summary>Deletes the planned files at <see cref="DownloadsMove.From"/> and the folders left empty there, the models folder included.</summary>
    /// <returns>The relative paths that could not be deleted, e.g. a file another program holds open.</returns>
    public static IReadOnlyList<string> DeleteSources(DownloadsMove move)
    {
        var left = move.Files.Where(relative => !TryDelete(Path.Combine(move.From, relative))).ToList();
        if (Directory.Exists(move.From))
        {
            foreach (var folder in Directory.EnumerateDirectories(move.From, "*", SearchOption.AllDirectories).OrderByDescending(f => f.Length))
            {
                TryDeleteEmptyFolder(folder);
            }

            TryDeleteEmptyFolder(move.From);
        }

        return left;
    }

    private static async Task<long> CopyFileAsync(string source, string target, long copied, IProgress<long>? copiedBytes, CancellationToken ct)
    {
        var buffer = new byte[BlockBytes];
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BlockBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BlockBytes, FileOptions.Asynchronous);
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            copiedBytes?.Report(copied);
        }

        return copied;
    }

    /// <summary>Creates <paramref name="folder"/>, adding each folder it had to create, outermost first, to <paramref name="created"/>.</summary>
    private static void CreateFolder(string folder, List<string> created)
    {
        var missing = new Stack<string>();
        for (var current = folder; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            missing.Push(current);
        }

        Directory.CreateDirectory(folder);
        created.AddRange(missing);
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, PathComparison.Current);

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: an empty folder does no harm, and the error that got us here matters more.
        }
    }
}
