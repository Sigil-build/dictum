namespace Dictum.Updates;

/// <summary>A newer release, and the package this installation would update from.</summary>
/// <param name="Package">Null when Dictum does not update this installation itself, or the release lacks the package,
/// its size or its attestation: then only the release page is offered.</param>
public sealed record UpdateOffer(ReleaseVersion Version, Uri Page, Installation Installation, ReleaseAsset? Package)
{
    /// <summary>The offer for <paramref name="release"/>, or null when it is not newer than <paramref name="current"/>.</summary>
    public static UpdateOffer? From(LatestRelease release, ReleaseVersion current, Installation installation)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(installation);
        if (ReleaseVersion.Parse(release.Tag) is not { } version || version <= current)
        {
            return null;
        }

        var attestation = ReleaseFiles.AttestationName(version);
        var package = ReleaseFiles.PackageName(installation.Kind, version) is { } name
                      && release.Assets.Any(a => a.Name == attestation)
            ? release.Assets.FirstOrDefault(a => a.Name == name && a.Size > 0)
            : null;
        return new UpdateOffer(version, release.Page, installation, package);
    }
}
