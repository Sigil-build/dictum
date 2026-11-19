using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.Tests.Updates;
using Dictum.App.Updates;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Core.Tests;
using Dictum.Core.Tests.Updates;
using Dictum.Setup;
using Dictum.Updates;
using Dictum.Updates.Attestation;
using Dictum.Updates.Install;

namespace Dictum.App.Tests.ViewModels;

/// <summary>About Dictum's update card: what it offers per install kind, and Update now on a fake GitHub and installer.</summary>
public sealed class AboutPageUpdateTests : IDisposable
{
    private const string Msi = "dictum-0.5.0-win-x64.msi";

    private static readonly byte[] Package = [.. Enumerable.Range(0, 4000).Select(i => (byte)(i * 11))];

    private readonly TempDirectory _dir = new();
    private readonly TestSigstore _sigstore = new();
    private readonly FakeHttpHandler _http = new();
    private readonly FakeReleases _found = new() { Next = FakeReleases.Found("v0.5.0") };
    private readonly FakeReleases _releases = new()
    {
        Next = new ReleaseLookup(new LatestRelease("v0.5.0", FakeReleases.Page("v0.5.0"),
            [new ReleaseAsset(Msi, Package.Length), new ReleaseAsset("dictum-0.5.0.intoto.jsonl", 12_000)])),
    };

    private readonly RecordingInstaller _installer = new(InstallKind.Msi);
    private readonly RecordingClipboard _clipboard = new();
    private readonly List<Uri> _urls = [];
    private readonly List<Action> _whenIdle = [];
    private UpdateChecker? _checker;
    private int _quits;
    private bool _dictating;

    public void Dispose()
    {
        _checker?.Dispose();
        _sigstore.Dispose();
        _dir.Dispose();
    }

    private static Installation Kind(InstallKind kind) => new(kind, kind == InstallKind.Portable ? null : @"C:\Programs\Dictum");

    private async Task<AboutPage> PageAsync(InstallKind kind = InstallKind.Msi, UpdateReport? report = null)
    {
        _checker = new UpdateChecker(_found, new UpdateCheckFile(_dir.File("update-check.json"), NullLog.Instance), new ManualTimers(),
            TimeProvider.System, "0.4.0", NullLog.Instance);
        await _checker.CheckNowAsync();
        var client = _http.Client();
        var downloader = new UpdateDownloader(client, new FileDownloader(client, [TimeSpan.Zero]), new AttestationVerifier(_sigstore.Trust),
            _dir.File("updates"));
        var installer = kind == InstallKind.Msi ? _installer : null;
        var service = new UpdateService(_releases, downloader, installer, Kind(kind), new ReleaseVersion(0, 4, 0));
        var page = new AboutPage(new AboutServices(
            "0.4.0", "Windows 11 (10.0.26200), x64", "win-x64", null, () => Task.FromResult(new HardwareInfo(null, 0)), _clipboard,
            (_, _) => { }, _urls.Add, NullLog.Instance, _checker,
            Service: service, IsDictating: () => _dictating, WhenIdle: _whenIdle.Add, Quit: () => _quits++, Report: report));
        await page.Loading;
        return page;
    }

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

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), args);

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static List<string?> Record(AboutPage page)
    {
        var texts = new List<string?>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AboutPage.WorkText))
            {
                texts.Add(page.WorkText);
            }
        };
        return texts;
    }

    [Fact]
    public async Task AnMsiInstallShowsUpdateNowWithTheSize()
    {
        using var page = await PageAsync();

        Assert.Equal(UpdateCard.CanUpdate, page.Card);
        Assert.Equal(Units.Size(Package.Length), page.UpdateSize);
        Assert.True(page.UpdateNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task APortableCopyShowsOnlyTheDownloadLink()
    {
        using var page = await PageAsync(InstallKind.Portable);

        Assert.Equal(UpdateCard.Portable, page.Card);
        Assert.False(page.UpdateNowCommand.CanExecute(null));
        page.OpenReleaseCommand.Execute(null);
        Assert.Equal([FakeReleases.Page("v0.5.0")], _urls);
    }

    [Fact]
    public async Task ThePackageManagerCardShowsTheCommands()
    {
        using var page = await PageAsync(InstallKind.PackageManager);

        Assert.Equal(UpdateCard.PackageManager, page.Card);
        Assert.Equal(ManualUpdate.PackageManagerCommands(new ReleaseVersion(0, 5, 0)), page.PackageManagerCommands);
        await page.CopyCommandsCommand.ExecuteAsync(null);
        Assert.Equal([ManualUpdate.PackageManagerCommands(new ReleaseVersion(0, 5, 0))], _clipboard.Texts);
    }

    [Fact]
    public async Task UpdateNowDownloadsVerifiesInstallsAndQuits()
    {
        Serve();
        using var page = await PageAsync();
        var texts = Record(page);

        await page.UpdateNowCommand.ExecuteAsync(null);

        Assert.StartsWith(Format(Strings.SettingsAboutDownloading, Units.Size(0), Units.Size(Package.Length)), texts[0], StringComparison.Ordinal);
        Assert.Equal([Strings.SettingsAboutVerifying, Strings.SettingsAboutInstalling], texts[^2..]);
        var (update, restart) = Assert.Single(_installer.Calls);
        Assert.Equal(RestartTarget.DesktopApp, restart);
        Assert.Equal(Package, File.ReadAllBytes(update.PackageFile));
        Assert.Equal(1, _quits);
        Assert.Equal(UpdateCard.Working, page.Card);
    }

    [Fact]
    public async Task ASecondClickWhileUpdatingStartsNothing()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>();
        _http.Respond((_, _) => answer.Task);
        using var page = await PageAsync();

        var first = page.UpdateNowCommand.ExecuteAsync(null);
        Assert.False(page.UpdateNowCommand.CanExecute(null));
        _ = page.UpdateNowCommand.ExecuteAsync(null);
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        await first;

        Assert.Single(_http.Requests);
        Assert.Empty(_installer.Calls);
    }

    [Fact]
    public async Task CheckNowIsDisabledWhileUpdating()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>();
        _http.Respond((_, _) => answer.Task);
        using var page = await PageAsync();

        var running = page.UpdateNowCommand.ExecuteAsync(null);

        Assert.False(page.CheckNowCommand.CanExecute(null));
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        await running;
        Assert.True(page.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnUnverifiedUpdateInstallsNothing()
    {
        Serve(sha256: new string('0', 64));
        using var page = await PageAsync();

        await page.UpdateNowCommand.ExecuteAsync(null);

        Assert.Equal(UpdateCard.Failed, page.Card);
        Assert.Equal(Strings.SettingsAboutUpdateNotVerified, page.UpdateProblem);
        Assert.Empty(_installer.Calls);
        Assert.Equal(0, _quits);
    }

    [Fact]
    public async Task AReleaseWithoutAnAttestationSaysSo()
    {
        _http.Respond(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var page = await PageAsync();

        await page.UpdateNowCommand.ExecuteAsync(null);

        Assert.Equal((UpdateCard.Failed, Strings.SettingsAboutUpdateNoPackage), (page.Card, page.UpdateProblem));
    }

    [Fact]
    public async Task CancelStopsTheDownload()
    {
        var hanging = new TaskCompletionSource();
        _http.Respond(_ => Ok(_sigstore.Mint(o =>
        {
            o.Version = "0.5.0";
            o.Subjects.Clear();
            o.Subjects.Add((Msi, Convert.ToHexStringLower(SHA256.HashData(Package))));
        }))).Respond(async (_, ct) =>
        {
            hanging.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("not reached");
        });
        using var page = await PageAsync();

        var running = page.UpdateNowCommand.ExecuteAsync(null);
        await hanging.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(page.CancelUpdateCommand.CanExecute(null));
        page.CancelUpdateCommand.Execute(null);
        await running;

        Assert.Equal(UpdateCard.CanUpdate, page.Card);
        Assert.Null(page.WorkText);
        Assert.Empty(_installer.Calls);
        Assert.True(page.UpdateNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheInstallWaitsForTheDictationToEnd()
    {
        Serve();
        _dictating = true;
        using var page = await PageAsync();

        var running = page.UpdateNowCommand.ExecuteAsync(null);
        await Eventually.WaitForAsync(() => _whenIdle.Count == 1, "the wait for idle");

        Assert.Equal(Strings.SettingsAboutWaitingForDictation, page.WorkText);
        Assert.False(page.CancelUpdateCommand.CanExecute(null));
        Assert.Empty(_installer.Calls);
        _whenIdle[0]();
        await running;
        Assert.Single(_installer.Calls);
        Assert.Equal(1, _quits);
    }

    [Fact]
    public async Task AnUnfinishedUpdateIsReportedOnStart()
    {
        var log = _dir.File(Path.Combine("updates", "0.5.0", "install.log"));
        using var page = await PageAsync(report: new UpdateReport(UpdateOutcome.DidNotFinish, "0.5.0", log));

        Assert.Equal(Format(Strings.SettingsAboutUpdateDidNotFinish, "0.5.0"), page.UpdateReportText);
        Assert.True(page.OpenUpdateLogCommand.CanExecute(null));
        page.OpenUpdateLogCommand.Execute(null);
        Assert.Equal([new Uri(log)], _urls);
    }

    [Fact]
    public async Task ARestartOfWindowsIsAskedFor()
    {
        using var page = await PageAsync(report: new UpdateReport(UpdateOutcome.NeedsRestart, "0.5.0", null));

        Assert.Equal(Format(Strings.SettingsAboutUpdateNeedsRestart, "0.5.0"), page.UpdateReportText);
        Assert.False(page.OpenUpdateLogCommand.CanExecute(null));
    }

    private sealed class RecordingInstaller(InstallKind kind) : IUpdateInstaller
    {
        public List<(DownloadedUpdate Update, RestartTarget Restart)> Calls { get; } = [];

        public InstallKind Kind => kind;

        public void Start(DownloadedUpdate update, int waitForProcess, RestartTarget restart) => Calls.Add((update, restart));
    }

    private sealed class RecordingClipboard : ITextClipboard
    {
        public List<string> Texts { get; } = [];

        public Task SetTextAsync(string text)
        {
            Texts.Add(text);
            return Task.CompletedTask;
        }
    }
}
