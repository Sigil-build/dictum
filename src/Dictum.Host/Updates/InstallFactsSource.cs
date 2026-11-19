using Dictum.Platform;
using Dictum.Platform.Windows;
using Dictum.Updates;

namespace Dictum.Host.Updates;

/// <summary>The real <see cref="InstallFacts"/> of this process, for <see cref="InstallDetector"/>.</summary>
public static class InstallFactsSource
{
    public static InstallFacts Current() => new(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        Environment.ProcessPath,
        ResolvedAppImage(InstallLocation.AppImage),
        OperatingSystem.IsWindows() ? MsiInstallFolder.Read() : null,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        CanWrite,
        File.Exists);

    /// <summary>Whether a file can be created in <paramref name="folder"/>: a real probe, created and deleted at once.</summary>
    public static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, $".dictum-write-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // DeleteOnClose already removed it, or it was never made.
            }
        }
    }

    /// <summary>A link to the AppImage is followed, so the file behind it is the one an update replaces.</summary>
    private static string? ResolvedAppImage(string? appImage)
    {
        if (appImage is null)
        {
            return null;
        }

        try
        {
            return File.ResolveLinkTarget(appImage, returnFinalTarget: true)?.FullName ?? appImage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return appImage;
        }
    }
}
