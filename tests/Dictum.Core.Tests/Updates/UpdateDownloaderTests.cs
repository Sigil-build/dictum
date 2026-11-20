using System.Net;
using System.Security.Cryptography;
using Dictum.Setup;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Attestation;

namespace Dictum.Core.Tests.Updates;

/// <summary>The download of an update against a fake GitHub: the attestation first, then the package it names.</summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private const string Msi = "dictum-0.7.1-win-x64.msi";
    private const string AttestationUrl = "https://github.com/Sigil-build/dictum/releases/download/v0.7.1/dictum-0.7.1.intoto.jsonl";
    private const string PackageUrl = "https://github.com/Sigil-build/dictum/releases/download/v0.7.1/" + Msi;

    private static readonly ReleaseVersion V071 = new(0, 7, 1);
    private static readonly byte[] Package = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 13))];

    private readonly TempDirectory _dir = new();
    private readonly TestSigstore _sigstore = new();
    private readonly FakeHttpHandler _http = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _sigstore.Dispose();
        _dir.Dispose();
    }

    private string Updates => _dir.File("updates");

    private static Installation MsiInstall => new(InstallKind.Msi, @"C:\Users\u\AppData\Local\Programs\Dictum");

    private static UpdateOffer Offer(Installation? installation = null, long? size = null) =>
        new(V071, new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1"), installation ?? MsiInstall,
            new ReleaseAsset(Msi, size ?? Package.Length));

    private UpdateDownloader Downloader()
    {
        var client = _http.Client();
        return new UpdateDownloader(client, new FileDownloader(client, [TimeSpan.Zero]), new AttestationVerifier(_sigstore.Trust), Updates);
    }

    private byte[] Attestation(string sha256) => _sigstore.Mint(o =>
    {
        o.Subjects.Clear();
        o.Subjects.Add((Msi, sha256));
    });

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public async Task TheAttestationComesFirstAndPinsThePackagesHash()
    {
        _http.Respond(_ => Ok(Attestation(Sha256(Package)))).Respond(_ => Ok(Package));

        var update = await Downloader().DownloadAsync(Offer(), null, _ct);

        Assert.Equal([AttestationUrl, PackageUrl], _http.Requests.Select(r => r.Request.RequestUri!.ToString()));
        Assert.Equal(Path.Combine(Updates, "0.7.1"), update.Folder);
        Assert.Equal(Path.Combine(Updates, "0.7.1", Msi), update.PackageFile);
        Assert.Equal(Package, File.ReadAllBytes(update.PackageFile));
    }

    [Fact]
    public async Task APackageWithAnotherHashIsDeleted()
    {
        _http.Respond(_ => Ok(Attestation(new string('0', 64)))).Respond(_ => Ok(Package));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(Offer(), null, _ct));

        Assert.Equal(UpdateFailure.NotVerified, ex.Failure);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Updates, "0.7.1")));
    }

    [Fact]
    public async Task AnUnverifiedAttestationDownloadsNoPackage()
    {
        using var other = new TestSigstore();
        _http.Respond(_ => Ok(other.Mint())).Respond(_ => Ok(Package));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(Offer(), null, _ct));

        Assert.Equal(UpdateFailure.NotVerified, ex.Failure);
        Assert.Equal([AttestationUrl], _http.Requests.Select(r => r.Request.RequestUri!.ToString()));
    }

    [Fact]
    public async Task AnAttestationThatDoesNotNameThePackageDownloadsNothing()
    {
        _http.Respond(_ => Ok(_sigstore.Mint(o => o.Subjects.Clear()))).Respond(_ => Ok(Package));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(Offer(), null, _ct));

        Assert.Equal(UpdateFailure.NotVerified, ex.Failure);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task AnAttestationOverOneMegabyteIsRefused()
    {
        var huge = new SizedStream(UpdateDownloader.MaxAttestationBytes + 1);
        _http.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(huge) });

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(Offer(), null, _ct));

        Assert.Equal(UpdateFailure.NotVerified, ex.Failure);
        Assert.True(huge.Taken <= UpdateDownloader.MaxAttestationBytes + 64 * 1024, $"read {huge.Taken} bytes");
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task AnUpdatesFolderThatCannotBeMadeCannotInstall()
    {
        File.WriteAllText(Updates, "a file where the updates folder goes");
        _http.Respond(_ => Ok(Attestation(Sha256(Package)))).Respond(_ => Ok(Package));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(Offer(), null, _ct));

        Assert.Equal(UpdateFailure.CannotInstall, ex.Failure);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task AMissingAttestationIsNoPackage()
    {
        _http.Respond(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(Offer(), null, _ct));

        Assert.Equal(UpdateFailure.NoPackage, ex.Failure);
    }

    [Fact]
    public async Task AnUnreachableGitHubIsADownloadFailure()
    {
        _http.Throw(new HttpRequestException("no route to host"));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(Offer(), null, _ct));

        Assert.Equal(UpdateFailure.Download, ex.Failure);
        Assert.Contains("no route to host", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOfferWithoutAPackageIsNoPackage()
    {
        var offer = Offer() with { Package = null };

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Downloader().DownloadAsync(offer, null, _ct));

        Assert.Equal(UpdateFailure.NoPackage, ex.Failure);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task CancellingKeepsThePartialFile()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        _http.Respond(_ => Ok(Attestation(Sha256(Package))))
            .Respond(_ =>
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(300));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Package[..1000])) };
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader().DownloadAsync(Offer(), null, cts.Token));

        Assert.Equal(1000, new FileInfo(FileDownloader.PartialPath(Path.Combine(Updates, "0.7.1", Msi))).Length);
    }

    // The AppImage's new file lands beside it, on the same filesystem, so the swap is a rename.
    [Fact]
    public async Task AnAppImageIsDownloadedBesideItself()
    {
        var folder = Directory.CreateDirectory(_dir.File("Applications")).FullName;
        var appImage = Path.Combine(folder, "Dictum.AppImage");
        const string name = "dictum-0.7.1-linux-x64.AppImage";
        var offer = Offer(new Installation(InstallKind.AppImage, appImage)) with { Package = new ReleaseAsset(name, Package.Length) };
        _http.Respond(_ => Ok(_sigstore.Mint(o =>
        {
            o.Subjects.Clear();
            o.Subjects.Add((name, Sha256(Package)));
        }))).Respond(_ => Ok(Package));

        var update = await Downloader().DownloadAsync(offer, null, _ct);

        Assert.Equal(Path.Combine(folder, ".Dictum.AppImage.update-0.7.1"), update.PackageFile);
        Assert.Equal(Path.Combine(Updates, "0.7.1"), update.Folder);
        Assert.True(Directory.Exists(update.Folder));
    }

    [Fact]
    public void ANewerReleaseWithItsPackageIsOffered()
    {
        var release = Release("v0.7.1", new ReleaseAsset(Msi, 29_000_000), new ReleaseAsset("dictum-0.7.1.intoto.jsonl", 12_000));

        var offer = UpdateOffer.From(release, new ReleaseVersion(0, 6, 0), MsiInstall);

        Assert.Equal(new ReleaseAsset(Msi, 29_000_000), offer!.Package);
        Assert.Equal(V071, offer.Version);
        Assert.Equal(MsiInstall, offer.Installation);
    }

    [Theory]
    [InlineData("v0.7.1")]
    [InlineData("v0.7.0")]
    [InlineData("v0.7.1-rc.1")]
    public void TheSameOrAnOlderVersionIsNotOffered(string tag) =>
        Assert.Null(UpdateOffer.From(Release(tag, new ReleaseAsset(Msi, 1)), V071, MsiInstall));

    [Fact]
    public void AReleaseWithoutTheSizeOffersNoPackage()
    {
        var release = Release("v0.7.1", new ReleaseAsset(Msi, 0), new ReleaseAsset("dictum-0.7.1.intoto.jsonl", 12_000));

        Assert.Null(UpdateOffer.From(release, new ReleaseVersion(0, 6, 0), MsiInstall)!.Package);
    }

    [Fact]
    public void AReleaseWithoutItsAttestationOffersNoPackage()
    {
        var release = Release("v0.7.1", new ReleaseAsset(Msi, 29_000_000));

        Assert.Null(UpdateOffer.From(release, new ReleaseVersion(0, 6, 0), MsiInstall)!.Package);
    }

    [Fact]
    public void APortableInstallIsOfferedWithoutAPackage()
    {
        var release = Release("v0.7.1", new ReleaseAsset(Msi, 29_000_000), new ReleaseAsset("dictum-0.7.1.intoto.jsonl", 12_000));

        var offer = UpdateOffer.From(release, new ReleaseVersion(0, 6, 0), Installation.Portable);

        Assert.NotNull(offer);
        Assert.Null(offer.Package);
    }

    private static LatestRelease Release(string tag, params ReleaseAsset[] assets) =>
        new(tag, new Uri($"https://github.com/Sigil-build/dictum/releases/tag/{tag}"), assets);

    private sealed class StallingStream(byte[] data) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>An endless-looking body that counts what was read from it.</summary>
    private sealed class SizedStream(long length) : Stream
    {
        public long Taken { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => Taken; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - Taken);
            Array.Fill(buffer, (byte)'{', offset, n);
            Taken += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
