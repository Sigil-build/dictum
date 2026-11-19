using System.Net;
using Dictum.Core.Diagnostics;
using Dictum.Setup;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Attestation;

namespace Dictum.Core.Tests.Updates;

/// <summary>The record of a started update, and how the next start judges it.</summary>
public sealed class PendingUpdateTests : IDisposable
{
    private static readonly ReleaseVersion V060 = new(0, 6, 0);
    private static readonly ReleaseVersion V071 = new(0, 7, 1);

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string Updates => _dir.File("updates");

    private PendingUpdateFile File => new(Updates, _log);

    private static PendingUpdate Pending(string version = "0.7.1") => new()
    {
        Version = version,
        Kind = InstallKind.Msi,
        Log = $@"C:\u\updates\{version}\install.log",
        Started = new DateTimeOffset(2026, 11, 19, 15, 13, 0, TimeSpan.Zero),
    };

    private void ExitCode(string version, string code)
    {
        Directory.CreateDirectory(Path.Combine(Updates, version));
        System.IO.File.WriteAllText(Path.Combine(Updates, version, PendingUpdateFile.ExitCodeFile), code + "\r\n");
    }

    [Fact]
    public void NoRecordReportsNothing() => Assert.Equal(new UpdateReport(UpdateOutcome.None, null, null), File.Take(V060));

    [Fact]
    public void ReachingTheTargetIsUpdated()
    {
        File.Save(Pending());

        Assert.Equal(new UpdateReport(UpdateOutcome.Updated, "0.7.1", Pending().Log), File.Take(V071));
    }

    // Installed by hand in between, or the runner never wrote its exit code: the running version decides.
    [Fact]
    public void ANewerRunningVersionCountsAsUpdated()
    {
        File.Save(Pending());
        ExitCode("0.7.1", "1603");

        Assert.Equal(UpdateOutcome.Updated, File.Take(new ReleaseVersion(0, 8, 0)).Outcome);
    }

    [Fact]
    public void AnOlderRunningVersionDidNotFinish()
    {
        File.Save(Pending());
        ExitCode("0.7.1", "1603");

        Assert.Equal(new UpdateReport(UpdateOutcome.DidNotFinish, "0.7.1", Pending().Log), File.Take(V060));
    }

    [Fact]
    public void NoExitCodeAndAnOlderVersionDidNotFinish()
    {
        File.Save(Pending());

        Assert.Equal(UpdateOutcome.DidNotFinish, File.Take(V060).Outcome);
    }

    [Fact]
    public void ExitCode3010NeedsARestart()
    {
        File.Save(Pending());
        ExitCode("0.7.1", "3010");

        Assert.Equal(new UpdateReport(UpdateOutcome.NeedsRestart, "0.7.1", Pending().Log), File.Take(V060));
    }

    [Fact]
    public void TakeDeletesTheRecord()
    {
        File.Save(Pending());

        File.Take(V071);

        Assert.Equal(UpdateOutcome.None, File.Take(V071).Outcome);
        Assert.False(System.IO.File.Exists(Path.Combine(Updates, PendingUpdateFile.Name)));
    }

    [Fact]
    public void TheRecordIsJsonInTheUpdatesFolder()
    {
        File.Save(Pending());

        var json = System.IO.File.ReadAllText(Path.Combine(Updates, "pending.json"));
        Assert.Contains("\"version\": \"0.7.1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"msi\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableRecordIsDroppedWithAWarning()
    {
        Directory.CreateDirectory(Updates);
        System.IO.File.WriteAllText(Path.Combine(Updates, PendingUpdateFile.Name), "{ not json");

        Assert.Equal(UpdateOutcome.None, File.Take(V060).Outcome);
        Assert.False(System.IO.File.Exists(Path.Combine(Updates, PendingUpdateFile.Name)));
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains(PendingUpdateFile.Name, StringComparison.Ordinal));
    }

    // Quitting Dictum or pressing Cancel while the package downloads: nothing was started, so the next start reports nothing.
    [Fact]
    public async Task CancellingDuringTheDownloadLeavesNoPendingRecord()
    {
        using var sigstore = new TestSigstore();
        using var cts = new CancellationTokenSource();
        var http = new FakeHttpHandler()
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sigstore.Mint()) })
            .Hang();
        var client = http.Client();
        var downloader = new UpdateDownloader(client, new FileDownloader(client, [TimeSpan.Zero]), new AttestationVerifier(sigstore.Trust), Updates);
        var offer = new UpdateOffer(V071, new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1"),
            new Installation(InstallKind.Msi, @"C:\u\Programs\Dictum"), new ReleaseAsset("dictum-0.7.1-win-x64.msi", 1000));
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(offer, null, cts.Token));

        Assert.Equal(UpdateOutcome.None, File.Take(V060).Outcome);
    }

    [Fact]
    public void CleanKeepsNewerVersionsOnly()
    {
        foreach (var version in new[] { "0.5.0", "0.6.0", "0.7.1", "not-a-version" })
        {
            Directory.CreateDirectory(Path.Combine(Updates, version));
        }

        File.Save(Pending());

        UpdatesFolder.Clean(Updates, V060, _log);

        Assert.Equal(["0.7.1", "not-a-version"], Directory.EnumerateDirectories(Updates).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(System.IO.File.Exists(Path.Combine(Updates, PendingUpdateFile.Name)));
    }

    [Fact]
    public void CleanWithoutAFolderDoesNothing() => UpdatesFolder.Clean(Updates, V060, _log);
}
