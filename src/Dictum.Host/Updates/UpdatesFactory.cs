using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Setup;
using Dictum.Updates;
using Dictum.Updates.Attestation;
using Dictum.Updates.Install;

namespace Dictum.Host.Updates;

/// <summary>The real <see cref="UpdateService"/>: GitHub, Sigstore's public trust roots, and the installer for this installation.</summary>
public static class UpdatesFactory
{
    /// <summary>For the process lifetime: the release lookup, and the attestation and the package.</summary>
    private static readonly Lazy<HttpClient> ApiHttp = new(GitHubReleases.CreateHttpClient);

    private static readonly Lazy<HttpClient> DownloadHttp = new(() => FileDownloader.CreateHttpClient("Dictum"));

    public static UpdateService Create(AppPaths paths, string appVersion, ILog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var installation = InstallDetector.Detect(InstallFactsSource.Current());
        var pending = new PendingUpdateFile(paths.UpdatesDirectory, log);
        var downloader = new UpdateDownloader(DownloadHttp.Value, new FileDownloader(DownloadHttp.Value),
            new AttestationVerifier(SigstoreTrust.PublicGood), paths.UpdatesDirectory);
        return new UpdateService(new GitHubReleases(ApiHttp.Value, appVersion), downloader, Installer(installation.Kind, pending),
            installation, ReleaseVersion.OfApp(appVersion));
    }

    /// <summary>The installer for <paramref name="kind"/>; null for the kinds Dictum leaves to the person or their package manager.</summary>
    public static IUpdateInstaller? Installer(InstallKind kind, PendingUpdateFile pending) => kind switch
    {
        InstallKind.Msi => new MsiInstaller(new DetachedRunner(), pending),
        InstallKind.AppImage => new AppImageInstaller(new DetachedRunner(), pending),
        InstallKind.Tarball => new TarballInstaller(new DetachedRunner(), pending),
        _ => null,
    };
}
