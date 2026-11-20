namespace Dictum.Updates;

/// <summary>How this copy of Dictum came to the computer, which decides how it updates.</summary>
public enum InstallKind
{
    /// <summary>Not installed: the zip, an unpacked tarball, a build from source. Dictum only links to the release.</summary>
    Portable,

    /// <summary>The per-user Windows installer; a newer MSI updates it.</summary>
    Msi,

    /// <summary>An AppImage in a folder the user may write to; the new AppImage replaces the file.</summary>
    AppImage,

    /// <summary>The tarball's <c>install.sh</c> put it in <c>~/.local/lib/dictum</c>; the new <c>install.sh</c> replaces it.</summary>
    Tarball,

    /// <summary>A package manager owns the files (the Arch package in <c>/opt/dictum</c>); Dictum says how to update.</summary>
    PackageManager,
}

/// <summary>What the detector needs to know about the running process; Host fills it from the real system, tests by hand.</summary>
/// <param name="ProcessPath">The running <c>dictum-gui</c> or <c>dictum</c>.</param>
/// <param name="AppImage">The AppImage file this process runs from, links resolved, or null.</param>
/// <param name="MsiInstallFolder">The folder the MSI recorded in the registry, or null.</param>
/// <param name="Home">The user's home folder.</param>
/// <param name="CanWrite">Whether the user may create a file in a folder.</param>
/// <param name="FileExists">Whether a file exists.</param>
public sealed record InstallFacts(
    bool IsWindows,
    bool IsLinux,
    string? ProcessPath,
    string? AppImage,
    string? MsiInstallFolder,
    string? Home,
    Func<string, bool> CanWrite,
    Func<string, bool> FileExists);

/// <summary>The install kind and what an update replaces: the MSI's folder, the AppImage file or <c>~/.local/lib/dictum</c>.</summary>
public sealed record Installation(InstallKind Kind, string? Target)
{
    public static readonly Installation Portable = new(InstallKind.Portable, null);
}

/// <summary>Tells the install kind from facts, never from version strings; the rules of the self-update design, §5.</summary>
public static class InstallDetector
{
    /// <summary>Where <c>install.sh</c> installs, under the home folder.</summary>
    public const string TarballFolder = ".local/lib/dictum";

    public static Installation Detect(InstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.ProcessPath is not { Length: > 0 } process)
        {
            return Installation.Portable;
        }

        if (facts.IsWindows)
        {
            return Windows(process, facts.MsiInstallFolder);
        }

        return facts.IsLinux ? Linux(process, facts) : Installation.Portable;
    }

    private static Installation Windows(string process, string? msiFolder)
    {
        var folder = Folder(process, windows: true);
        return msiFolder is { Length: > 0 } && folder is not null
            && string.Equals(folder, msiFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? new Installation(InstallKind.Msi, folder)
            : Installation.Portable;
    }

    private static Installation Linux(string process, InstallFacts facts)
    {
        if (facts.AppImage is { Length: > 0 } appImage)
        {
            return Folder(appImage, windows: false) is { } appImageFolder && facts.CanWrite(appImageFolder)
                ? new Installation(InstallKind.AppImage, appImage)
                : Installation.Portable;
        }

        if (Folder(process, windows: false) is not { } folder)
        {
            return Installation.Portable;
        }

        if (facts.Home is { Length: > 0 } home)
        {
            var tarball = home.TrimEnd('/') + "/" + TarballFolder;
            if (folder == tarball)
            {
                return facts.FileExists(tarball + "/uninstall.sh") ? new Installation(InstallKind.Tarball, tarball) : Installation.Portable;
            }

            if (IsInside(folder, home))
            {
                return Installation.Portable;
            }
        }

        return facts.CanWrite(folder) ? Installation.Portable : new Installation(InstallKind.PackageManager, null);
    }

    private static bool IsInside(string folder, string home)
    {
        var root = home.TrimEnd('/');
        return folder == root || folder.StartsWith(root + "/", StringComparison.Ordinal);
    }

    /// <summary>The folder of <paramref name="path"/> by the rules of the OS the facts describe, not of the one running.</summary>
    private static string? Folder(string path, bool windows)
    {
        var end = windows ? path.LastIndexOfAny(['\\', '/']) : path.LastIndexOf('/');
        return end > 0 ? path[..end] : null;
    }
}
