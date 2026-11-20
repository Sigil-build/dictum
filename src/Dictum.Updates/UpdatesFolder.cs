using Dictum.Core.Diagnostics;

namespace Dictum.Updates;

/// <summary><c>&lt;data&gt;/updates/</c>: one folder per version that was downloaded, and <c>pending.json</c>.</summary>
public static class UpdatesFolder
{
    /// <summary>
    /// Deletes the folders of versions up to <paramref name="current"/>: installed, or older than what runs. Newer ones stay,
    /// so a download that was stopped resumes. Never throws.
    /// </summary>
    public static void Clean(string updatesDirectory, ReleaseVersion current, ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (!Directory.Exists(updatesDirectory))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(updatesDirectory))
            {
                if (ReleaseVersion.Parse(Path.GetFileName(folder)) is { } version && version <= current)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Old update files were not all deleted from {updatesDirectory}: {ex.Message}");
        }
    }
}
