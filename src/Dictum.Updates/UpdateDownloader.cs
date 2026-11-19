using System.Globalization;
using System.Net;
using Dictum.Setup;
using Dictum.Updates.Attestation;

namespace Dictum.Updates;

/// <summary>A verified package on disk, ready for its installer.</summary>
/// <param name="Folder"><c>&lt;data&gt;/updates/&lt;V&gt;/</c>: the install log and the runner's exit code go there.</param>
/// <param name="PackageFile">The package: in <paramref name="Folder"/>, or beside the AppImage it replaces.</param>
public sealed record DownloadedUpdate(UpdateOffer Offer, string Folder, string PackageFile);

/// <summary>
/// Fetches the release's attestation, verifies it, and only then downloads the package with the SHA-256 it names, through
/// <see cref="FileDownloader"/> (resumable, its hash checked before it is moved into place).
/// </summary>
public sealed class UpdateDownloader(HttpClient http, FileDownloader files, AttestationVerifier verifier, string updatesDirectory)
{
    /// <summary>A real attestation is about 12 KB; anything past this is not one.</summary>
    public const int MaxAttestationBytes = 1 << 20;

    private static readonly TimeSpan AttestationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Room for the package and a tenth more, so the update does not fill the disk to its last byte.</summary>
    private const double Headroom = 1.1;

    public async Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.Package is not { } package)
        {
            throw new UpdateException(UpdateFailure.NoPackage, $"Dictum {offer.Version} has no package for this installation.");
        }

        var version = offer.Version;
        var folder = Path.Combine(updatesDirectory, version.ToString());
        var target = PackagePath(offer.Installation, folder, package.Name, version);

        var bundle = await AttestationAsync(version, ct).ConfigureAwait(false);
        var result = verifier.Verify(bundle, version);
        if (!result.Verified)
        {
            throw new UpdateException(UpdateFailure.NotVerified, $"The attestation of {version} did not verify ({result.Failure}): {result.Detail}");
        }

        if (!result.Digests.TryGetValue(package.Name, out var sha256))
        {
            throw new UpdateException(UpdateFailure.NotVerified, $"The attestation of {version} does not name {package.Name}.");
        }

        Directory.CreateDirectory(folder);
        EnsureRoom(Path.GetDirectoryName(target)!, package.Size);
        var source = new DownloadSource(package.Name, ReleaseFiles.DownloadUrl(version, package.Name).AbsoluteUri, sha256, package.Size);
        try
        {
            await files.DownloadAsync(source, target, progress, ct).ConfigureAwait(false);
        }
        catch (DownloadException ex) when (ex.WrongHash)
        {
            throw new UpdateException(UpdateFailure.NotVerified, ex.Message, ex);
        }
        catch (Exception ex) when (ex is DownloadException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailure.Download, ex.Message, ex);
        }

        return new DownloadedUpdate(offer, folder, target);
    }

    /// <summary>An AppImage's package lands beside it, hidden, so the swap is a rename on one filesystem.</summary>
    private static string PackagePath(Installation installation, string folder, string name, ReleaseVersion version) =>
        installation is { Kind: InstallKind.AppImage, Target: { } appImage }
            ? Path.Combine(Path.GetDirectoryName(appImage)!, $".{Path.GetFileName(appImage)}.update-{version}")
            : Path.Combine(folder, name);

    private async Task<byte[]> AttestationAsync(ReleaseVersion version, CancellationToken ct)
    {
        var url = ReleaseFiles.DownloadUrl(version, ReleaseFiles.AttestationName(version));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(AttestationTimeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateException(UpdateFailure.NoPackage, $"Dictum {version} has no attestation ({url} answered 404).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateFailure.Download,
                    string.Create(CultureInfo.InvariantCulture, $"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}."));
            }

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            return await ReadCappedAsync(body, version, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Download, $"no answer from {url} within {AttestationTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateFailure.Download, $"Could not download {url}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateFailure.Download, $"Could not download {url}: {ex.Message}", ex);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream body, ReleaseVersion version, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxAttestationBytes)
            {
                throw new UpdateException(UpdateFailure.NotVerified, $"The attestation of {version} is larger than {MaxAttestationBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void EnsureRoom(string folder, long size)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // Unknown: the download itself reports a full disk.
        }

        if (free < size * Headroom)
        {
            throw new UpdateException(UpdateFailure.NoRoom,
                string.Create(CultureInfo.InvariantCulture, $"{free} bytes free in {folder}, {size} needed for the update."));
        }
    }
}
