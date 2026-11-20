using Dictum.Setup;
using Dictum.Updates.Install;

namespace Dictum.Updates;

public enum UpdateStage
{
    Downloading,

    /// <summary>The package is all there; its hash is being checked against the attestation.</summary>
    Verifying,

    Installing,
}

public sealed record UpdateProgress(UpdateStage Stage, long Bytes, long Total);

/// <summary>
/// Finds, downloads and installs an update: the one path the desktop app and <c>dictum update</c> share. Nothing is
/// downloaded until <see cref="DownloadAsync"/>, and nothing installed until <see cref="Install"/>.
/// </summary>
/// <param name="installer">The installer for this installation's kind; null when Dictum does not update it itself.</param>
/// <param name="current">This build's version; null for a build without one, which is offered nothing.</param>
public sealed class UpdateService(
    IReleaseSource releases, UpdateDownloader downloader, IUpdateInstaller? installer, Installation installation, ReleaseVersion? current)
{
    public Installation Installation { get; } = installation;

    public ReleaseVersion? Current { get; } = current;

    /// <summary>The newer release for this installation, if any, and the lookup that found it or why it failed.</summary>
    public async Task<(UpdateOffer? Offer, ReleaseLookup Lookup)> FindAsync(CancellationToken ct)
    {
        var lookup = await releases.LatestAsync(ct).ConfigureAwait(false);
        var offer = lookup.Release is { } release && Current is { } version ? UpdateOffer.From(release, version, Installation) : null;
        return (offer, lookup);
    }

    /// <summary>
    /// Downloads the attestation and then the package it pins; <paramref name="progress"/> hears
    /// <see cref="UpdateStage.Downloading"/> with the bytes so far, then <see cref="UpdateStage.Verifying"/> once the last
    /// byte is in. Throws <see cref="UpdateException"/>, or <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var total = offer.Package?.Size ?? 0;
        progress?.Report(new UpdateProgress(UpdateStage.Downloading, 0, total));
        return downloader.DownloadAsync(offer, progress is null ? null : new Forward(progress), ct);
    }

    /// <summary>
    /// Hands <paramref name="update"/> to the installer, which waits for this process to exit; the caller then exits.
    /// Throws <see cref="UpdateException"/>: <see cref="UpdateFailure.NoPackage"/> when this installation is not one Dictum
    /// updates, <see cref="UpdateFailure.CannotInstall"/> when the installer cannot start.
    /// </summary>
    public void Install(DownloadedUpdate update, RestartTarget restart)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (installer is null || installer.Kind != update.Offer.Installation.Kind)
        {
            throw new UpdateException(UpdateFailure.NoPackage, $"Dictum does not update a {update.Offer.Installation.Kind} installation itself.");
        }

        installer.Start(update, Environment.ProcessId, restart);
    }

    /// <summary>The file download's progress as the update's, on the reporting thread.</summary>
    private sealed class Forward(IProgress<UpdateProgress> progress) : IProgress<DownloadProgress>
    {
        private bool _verifying;

        public void Report(DownloadProgress value)
        {
            if (_verifying)
            {
                return;
            }

            _verifying = value.Total > 0 && value.Bytes >= value.Total;
            progress.Report(new UpdateProgress(_verifying ? UpdateStage.Verifying : UpdateStage.Downloading, value.Bytes, value.Total));
        }
    }
}
