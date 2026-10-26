using Dictum.Core;

namespace Dictum.Setup;

/// <summary>The check every download makes first: a configured downloads folder that is gone is an error, never created or replaced.</summary>
/// <remarks>
/// A folder on an unplugged drive would otherwise be created anew on the system drive, or the models would silently go to the
/// data folder; either way the user's choice would be lost without a word. The data folder itself is still created on demand.
/// </remarks>
public static class DownloadsFolder
{
    /// <summary>What to do about a missing folder, said the same way by downloads, the doctor and the CLI.</summary>
    public const string MissingHint = "Connect the drive, or choose another folder: Settings → Models → Change…, or 'dictum models folder <path>'.";

    public static string MissingError(string folder) => $"The downloads folder {folder} is not available.";

    public static string MissingMessage(string folder) => $"{MissingError(folder)} {MissingHint}";

    /// <exception cref="DownloadException">The configured downloads folder does not exist.</exception>
    public static void EnsureAvailable(AppPaths paths)
    {
        if (paths.IsDownloadsDirectoryMissing)
        {
            throw new DownloadException(MissingMessage(paths.DownloadsDirectory));
        }
    }
}
