using System.Formats.Tar;
using System.IO.Compression;

namespace Dictum.Updates.Install;

/// <summary>
/// Unpacks a release tarball, and only what a release tarball holds: files, folders and links under its one root
/// folder. The hash already proved the file is the release's; this keeps a wrong entry from writing anywhere else.
/// </summary>
public static class SafeTar
{
    /// <summary>The permission bits; set-id and sticky bits are dropped.</summary>
    private const UnixFileMode Permissions = (UnixFileMode)0x1FF;

    /// <summary>
    /// Unpacks <paramref name="tarGz"/> into <paramref name="folder"/> and returns <c>folder/expectedRoot</c>, replacing an
    /// earlier copy. Throws <see cref="UpdateException"/> (<see cref="UpdateFailure.NotVerified"/>) for an entry outside
    /// <paramref name="expectedRoot"/>, an absolute or <c>..</c> path, a hard link, a symbolic link that is absolute or
    /// climbs with <c>..</c>, a device or a fifo, or a file that is not a gzipped tar; then nothing of the root stays.
    /// File modes are kept.
    /// </summary>
    public static string Extract(string tarGz, string folder, string expectedRoot)
    {
        var root = Path.Combine(Path.GetFullPath(folder), expectedRoot);
        Delete(root);
        try
        {
            using var file = File.OpenRead(tarGz);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                Write(entry, root, expectedRoot);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            Delete(root);
            throw new UpdateException(UpdateFailure.NotVerified, $"{tarGz} is not a readable tarball: {ex.Message}", ex);
        }
        catch
        {
            Delete(root);
            throw;
        }

        return root;
    }

    private static void Write(TarEntry entry, string root, string expectedRoot)
    {
        if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
        {
            return;
        }

        var target = Destination(entry.Name, root, expectedRoot);
        switch (entry.EntryType)
        {
            case TarEntryType.Directory:
                Directory.CreateDirectory(target);
                break;
            case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(target, entry.Mode & Permissions);
                }

                break;
            case TarEntryType.SymbolicLink:
                // A relative link without ".." can only point at or below its own folder, so a chain of them stays in
                // the root too. The release's tarball has no links at all.
                if (entry.LinkName.Length == 0 || entry.LinkName.StartsWith('/') || Path.IsPathRooted(entry.LinkName)
                    || entry.LinkName.Split('/').Contains(".."))
                {
                    throw Refused(entry, $"links to {entry.LinkName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.CreateSymbolicLink(target, entry.LinkName);
                break;
            default:
                throw Refused(entry, $"is a {entry.EntryType} entry");
        }
    }

    /// <summary>Where an entry goes: under the root, never above it.</summary>
    private static string Destination(string name, string root, string expectedRoot)
    {
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        if (name.StartsWith('/') || Path.IsPathRooted(name) || parts.Length == 0 || parts[0] != expectedRoot
            || parts.Any(p => p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new UpdateException(UpdateFailure.NotVerified, $"The tarball's entry {name} is not under {expectedRoot}/.");
        }

        var target = Path.GetFullPath(Path.Combine([root, .. parts[1..]]));
        return target == root || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? target
            : throw new UpdateException(UpdateFailure.NotVerified, $"The tarball's entry {name} is not under {expectedRoot}/.");
    }

    private static UpdateException Refused(TarEntry entry, string what) =>
        new(UpdateFailure.NotVerified, $"The tarball's entry {entry.Name} {what}; a release tarball has none.");

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
