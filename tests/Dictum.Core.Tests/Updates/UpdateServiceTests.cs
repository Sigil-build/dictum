using System.Net;
using System.Security.Cryptography;
using Dictum.Setup;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Attestation;
using Dictum.Updates.Install;

namespace Dictum.Core.Tests.Updates;

/// <summary>The service the desktop app and the CLI share, on a fake GitHub and a fake installer.</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const string Msi = "dictum-0.7.1-win-x64.msi";
    private const string Attestation = "dictum-0.7.1.intoto.jsonl";

    private static readonly ReleaseVersion V060 = new(0, 6, 0);
    private static readonly Uri Page = new("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1");
    private static readonly Installation MsiInstall = new(InstallKind.Msi, @"C:\Users\u\AppData\Local\Programs\Dictum");
    private static readonly byte[] Package = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))];

    private readonly TempDirectory _dir = new();
    private readonly TestSigstore _sigstore = new();
    private readonly FakeHttpHandler _http = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _sigstore.Dispose();
        _dir.Dispose();
    }

    private static LatestRelease Release(string tag = "v0.7.1") =>
        new(tag, Page, [new ReleaseAsset(Msi, Package.Length), new ReleaseAsset(Attestation, 12_000)]);

    private UpdateService Service(ReleaseLookup lookup, IUpdateInstaller? installer = null, ReleaseVersion? current = null)
    {
        var client = _http.Client();
        var downloader = new UpdateDownloader(client, new FileDownloader(client, [TimeSpan.Zero]), new AttestationVerifier(_sigstore.Trust), _dir.File("updates"));
        return new UpdateService(new FixedReleases(lookup), downloader, installer, MsiInstall, current ?? V060);
    }

    [Fact]
    public async Task FindOffersANewerRelease()
    {
        var (offer, lookup) = await Service(new ReleaseLookup(Release())).FindAsync(_ct);

        Assert.Equal(UpdateCheckFailure.None, lookup.Failure);
        Assert.NotNull(offer);
        Assert.Equal(new ReleaseVersion(0, 7, 1), offer.Version);
        Assert.Equal(new ReleaseAsset(Msi, Package.Length), offer.Package);
        Assert.Same(MsiInstall, offer.Installation);
    }

    [Fact]
    public async Task FindOffersNothingForTheSameVersion()
    {
        var (offer, _) = await Service(new ReleaseLookup(Release("v0.6.0"))).FindAsync(_ct);

        Assert.Null(offer);
    }

    [Fact]
    public async Task FindReportsTheLookupsFailure()
    {
        var failed = new ReleaseLookup(null, UpdateCheckFailure.Unreachable, Detail: "no route");

        var (offer, lookup) = await Service(failed).FindAsync(_ct);

        Assert.Null(offer);
        Assert.Same(failed, lookup);
    }

    [Fact]
    public async Task DownloadReportsDownloadingThenVerifying()
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(Package));
        _http.Respond(_ => Ok(_sigstore.Mint(o =>
        {
            o.Subjects.Clear();
            o.Subjects.Add((Msi, sha256));
        }))).Respond(_ => Ok(Package));
        var service = Service(new ReleaseLookup(Release()));
        var (offer, _) = await service.FindAsync(_ct);
        var stages = new List<UpdateProgress>();

        var update = await service.DownloadAsync(offer!, new Recorder(stages), _ct);

        Assert.Equal(Package, File.ReadAllBytes(update.PackageFile));
        Assert.Equal(new UpdateProgress(UpdateStage.Downloading, 0, Package.Length), stages[0]);
        Assert.Equal(new UpdateProgress(UpdateStage.Verifying, Package.Length, Package.Length), stages[^1]);
        Assert.All(stages[..^1], s => Assert.Equal(UpdateStage.Downloading, s.Stage));
    }

    [Fact]
    public void InstallWithoutAnInstallerIsNoPackage()
    {
        var update = new DownloadedUpdate(new UpdateOffer(new ReleaseVersion(0, 7, 1), Page, MsiInstall, new ReleaseAsset(Msi, 1)), "f", "f/p");

        var ex = Assert.Throws<UpdateException>(() => Service(new ReleaseLookup(Release())).Install(update, RestartTarget.DesktopApp));

        Assert.Equal(UpdateFailure.NoPackage, ex.Failure);
    }

    [Fact]
    public void InstallWithAnInstallerOfAnotherKindIsNoPackage()
    {
        var update = new DownloadedUpdate(new UpdateOffer(new ReleaseVersion(0, 7, 1), Page, MsiInstall, new ReleaseAsset(Msi, 1)), "f", "f/p");
        var installer = new RecordingInstaller(InstallKind.Tarball);

        var ex = Assert.Throws<UpdateException>(() => Service(new ReleaseLookup(Release()), installer).Install(update, RestartTarget.DesktopApp));

        Assert.Equal(UpdateFailure.NoPackage, ex.Failure);
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public void InstallHandsThisProcessToTheInstaller()
    {
        var update = new DownloadedUpdate(new UpdateOffer(new ReleaseVersion(0, 7, 1), Page, MsiInstall, new ReleaseAsset(Msi, 1)), "f", "f/p");
        var installer = new RecordingInstaller(InstallKind.Msi);

        Service(new ReleaseLookup(Release()), installer).Install(update, RestartTarget.Daemon);

        Assert.Equal([(update, Environment.ProcessId, RestartTarget.Daemon)], installer.Calls);
    }

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private sealed class FixedReleases(ReleaseLookup lookup) : IReleaseSource
    {
        public Task<ReleaseLookup> LatestAsync(CancellationToken ct) => Task.FromResult(lookup);
    }

    /// <summary>Records at once, on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class Recorder(List<UpdateProgress> stages) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => stages.Add(value);
    }

    private sealed class RecordingInstaller(InstallKind kind) : IUpdateInstaller
    {
        public List<(DownloadedUpdate, int, RestartTarget)> Calls { get; } = [];

        public InstallKind Kind => kind;

        public void Start(DownloadedUpdate update, int waitForProcess, RestartTarget restart) => Calls.Add((update, waitForProcess, restart));
    }
}
