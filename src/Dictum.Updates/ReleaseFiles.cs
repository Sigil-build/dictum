namespace Dictum.Updates;

/// <summary>
/// The names and URLs of a release's files, as <c>release.yml</c> publishes them. Dictum builds them itself; the URLs in
/// GitHub's answer are not used.
/// </summary>
public static class ReleaseFiles
{
    private const string Downloads = "https://github.com/Sigil-build/dictum/releases/download/";

    /// <summary>The package an install of <paramref name="kind"/> updates from; null when Dictum does not update it.</summary>
    public static string? PackageName(InstallKind kind, ReleaseVersion version) => kind switch
    {
        InstallKind.Msi => $"dictum-{version}-win-x64.msi",
        InstallKind.AppImage => $"dictum-{version}-linux-x64.AppImage",
        InstallKind.Tarball => $"dictum-{version}-linux-x64.tar.gz",
        _ => null,
    };

    /// <summary>The release's provenance attestation, a Sigstore bundle naming every release file.</summary>
    public static string AttestationName(ReleaseVersion version) => $"dictum-{version}.intoto.jsonl";

    /// <summary>The root folder inside the release tarball.</summary>
    public static string TarballRoot(ReleaseVersion version) => $"dictum-{version}-linux-x64";

    public static Uri DownloadUrl(ReleaseVersion version, string name) => new($"{Downloads}v{version}/{name}");

    public static Uri PkgbuildUrl(ReleaseVersion version) => DownloadUrl(version, "PKGBUILD");
}
