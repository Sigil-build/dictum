using System.Net;
using System.Security.Cryptography;
using Dictum.Cli.Commands;
using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Core.Tests;
using Dictum.Core.Tests.Updates;
using Dictum.Setup;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Attestation;
using Dictum.Updates.Install;

namespace Dictum.Cli.Tests;

/// <summary><c>dictum update</c> on a fake GitHub, a test Sigstore and a recording installer, in the sandbox's data folder.</summary>
public sealed class UpdateCommandTests : IDisposable
{
    private const string Msi = "dictum-0.5.0-win-x64.msi";
    private const string Page = "https://github.com/Sigil-build/dictum/releases/tag/v0.5.0";
    private const string Target = @"C:\Programs\Dictum";

    private static readonly byte[] Package = [.. Enumerable.Range(0, 4000).Select(i => (byte)(i * 7))];

    private readonly TempDirectory _dir = new();
    private readonly TestSigstore _sigstore = new();
    private readonly FakeHttpHandler _http = new();
    private readonly StubReleases _releases = new();
    private readonly RecordingInstaller _installer = new();
    private readonly PendingUpdateFile _pending = new(AppPaths.FromEnvironment().UpdatesDirectory, NullLog.Instance);
    private InstallKind _kind = InstallKind.Msi;
    private RestartTarget _running = RestartTarget.DesktopApp;

    public UpdateCommandTests()
    {
        _releases.Next = Found("v0.5.0");
        UpdateCommand.Service = _ => Service();
        UpdateCommand.Running = (_, _) => Task.FromResult(_running);
    }

    public void Dispose()
    {
        UpdateCommand.Service = null;
        UpdateCommand.Running = null;
        _pending.Discard();
        _sigstore.Dispose();
        _dir.Dispose();
    }

    private UpdateService Service()
    {
        var client = _http.Client();
        var downloader = new UpdateDownloader(client, new FileDownloader(client, [TimeSpan.Zero]), new AttestationVerifier(_sigstore.Trust),
            _dir.File("updates"));
        var installation = new Installation(_kind, _kind == InstallKind.Portable ? null : Target);
        return new UpdateService(_releases, downloader, _kind == InstallKind.Msi ? _installer : null, installation, new ReleaseVersion(0, 4, 0));
    }

    private static ReleaseLookup Found(string tag) => new(new LatestRelease(tag, new Uri($"https://github.com/Sigil-build/dictum/releases/tag/{tag}"),
        [new ReleaseAsset(Msi, Package.Length), new ReleaseAsset("dictum-0.5.0.intoto.jsonl", 12_000)]));

    private void Serve(string? sha256 = null)
    {
        var digest = sha256 ?? Convert.ToHexStringLower(SHA256.HashData(Package));
        _http.Respond(_ => Ok(_sigstore.Mint(o =>
        {
            o.Version = "0.5.0";
            o.Subjects.Clear();
            o.Subjects.Add((Msi, digest));
        }))).Respond(_ => Ok(Package));
    }

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        using var console = new ConsoleCapture();
        var code = await console.RunAsync(["update", .. args]);
        return (code, console.Out.ToString(), console.Error.ToString());
    }

    [Fact]
    public async Task CheckSaysUpToDateWithZero()
    {
        _releases.Next = Found("v0.4.0");

        var (code, output, _) = await RunAsync("--check");

        Assert.Equal(0, code);
        Assert.Contains("Dictum 0.4.0 is up to date.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckSaysAvailableWithSix()
    {
        var (code, output, _) = await RunAsync("--check");

        Assert.Equal(6, code);
        Assert.Contains($"Dictum 0.4.0 → 0.5.0 is available. {Page}", output, StringComparison.Ordinal);
        Assert.Empty(_http.Requests);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task AFailedCheckExitsOne()
    {
        _releases.Next = new ReleaseLookup(null, UpdateCheckFailure.Unreachable, Detail: "No such host is known.");

        var (code, _, error) = await RunAsync("--check");

        Assert.Equal(1, code);
        Assert.Contains("error: Could not check for updates: No such host is known.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortableCopyPrintsTheLinkAndChangesNothing()
    {
        _kind = InstallKind.Portable;

        var (code, output, _) = await RunAsync();

        Assert.Equal(0, code);
        Assert.Contains($"Dictum 0.4.0 → 0.5.0 is available. {Page}", output, StringComparison.Ordinal);
        Assert.Contains("This copy of Dictum was not installed, so it does not update itself. Download the new version from the page above.",
            output, StringComparison.Ordinal);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task ThePackageManagerPrintsTheCommands()
    {
        _kind = InstallKind.PackageManager;

        var (code, output, _) = await RunAsync();

        Assert.Equal(0, code);
        Assert.Contains("Dictum was installed by your package manager. Build the new package:", output, StringComparison.Ordinal);
        foreach (var line in ManualUpdate.PackageManagerCommands(new ReleaseVersion(0, 5, 0)).Split('\n'))
        {
            Assert.Contains($"  {line}", output, StringComparison.Ordinal);
        }

        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task NoTerminalWithoutYesRefuses()
    {
        var (code, output, error) = await RunAsync();

        Assert.Equal(2, code);
        Assert.Contains("Installed as: MSI (C:\\Programs\\Dictum)", output, StringComparison.Ordinal);
        Assert.Contains("error: Input is not a terminal. Run 'dictum update --yes' to update without asking.", error, StringComparison.Ordinal);
        Assert.Empty(_http.Requests);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task YesDownloadsVerifiesAndStartsTheInstaller()
    {
        Serve();

        var (code, output, error) = await RunAsync("--yes");

        Assert.Equal(0, code);
        Assert.Contains($"Dictum will download {Msi} ({SetupRunner.Format(Package.Length)}), verify it, install it with Windows Installer and restart Dictum.",
            output, StringComparison.Ordinal);
        var (update, process, restart) = Assert.Single(_installer.Calls);
        Assert.Equal(Environment.ProcessId, process);
        Assert.Equal(RestartTarget.DesktopApp, restart);
        Assert.Equal(Package, File.ReadAllBytes(update.PackageFile));
        Assert.Contains("Downloading", error, StringComparison.Ordinal);
        Assert.Contains("Verifying", error, StringComparison.Ordinal);
        Assert.Contains(
            $"Installing 0.5.0 now; Dictum starts again when it is done. Log: {Path.Combine(update.Folder, "install.log")}",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNothingRunningNothingStartsAfterwards()
    {
        _running = RestartTarget.None;
        Serve();

        var (code, output, _) = await RunAsync("-y");

        Assert.Equal(0, code);
        Assert.Contains("verify it and install it with Windows Installer.", output, StringComparison.Ordinal);
        Assert.Equal(RestartTarget.None, Assert.Single(_installer.Calls).Restart);
        Assert.Contains("Installing 0.5.0 now. Log: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnverifiedReleaseExitsSeven()
    {
        Serve(sha256: new string('0', 64));

        var (code, _, error) = await RunAsync("--yes");

        Assert.Equal(7, code);
        Assert.Contains("error: ", error, StringComparison.Ordinal);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task AFailedDownloadExitsFive()
    {
        _http.Respond(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var (code, _, error) = await RunAsync("--yes");

        Assert.Equal(5, code);
        Assert.Contains("error: ", error, StringComparison.Ordinal);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task APreviousUnfinishedUpdateIsReported()
    {
        _pending.Save(new PendingUpdate
        {
            Version = "0.5.0", Kind = InstallKind.Msi, Log = @"C:\data\updates\0.5.0\install.log", Started = DateTimeOffset.Now.AddHours(-2),
        });

        var (code, output, _) = await RunAsync("--check");

        Assert.Equal(6, code);
        Assert.StartsWith(@"The update to 0.5.0 did not finish. Log: C:\data\updates\0.5.0\install.log", output, StringComparison.Ordinal);
        Assert.Null(_pending.Installing(new ReleaseVersion(0, 4, 0), DateTimeOffset.Now));
        Assert.False(File.Exists(Path.Combine(AppPaths.FromEnvironment().UpdatesDirectory, PendingUpdateFile.Name)));
    }

    // Run again right after the last one handed over: its runner is still at work, so its record stays for the app it restarts.
    [Fact]
    public async Task AnUpdateStillInstallingIsNotStartedAgain()
    {
        _pending.Save(new PendingUpdate
        {
            Version = "0.5.0", Kind = InstallKind.Msi, Log = @"C:\data\updates\0.5.0\install.log", Started = DateTimeOffset.Now,
        });

        var (code, _, error) = await RunAsync("--yes");

        Assert.Equal(1, code);
        Assert.Contains(@"error: The update to 0.5.0 is still being installed. Log: C:\data\updates\0.5.0\install.log", error, StringComparison.Ordinal);
        Assert.Empty(_installer.Calls);
        Assert.NotNull(_pending.Installing(new ReleaseVersion(0, 4, 0), DateTimeOffset.Now));
    }

    private sealed class StubReleases : IReleaseSource
    {
        public ReleaseLookup Next { get; set; } = new(null);

        public Task<ReleaseLookup> LatestAsync(CancellationToken ct) => Task.FromResult(Next);
    }

    private sealed class RecordingInstaller : IUpdateInstaller
    {
        public List<(DownloadedUpdate Update, int Process, RestartTarget Restart)> Calls { get; } = [];

        public InstallKind Kind => InstallKind.Msi;

        public void Start(DownloadedUpdate update, int waitForProcess, RestartTarget restart) => Calls.Add((update, waitForProcess, restart));
    }
}
