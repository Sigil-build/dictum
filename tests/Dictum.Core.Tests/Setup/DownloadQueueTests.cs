using System.Net;
using System.Security.Cryptography;
using Dictum.Setup;

namespace Dictum.Core.Tests.Setup;

public class DownloadQueueTests : IDisposable
{
    private static readonly byte[] DataA = [.. Enumerable.Range(0, 4000).Select(i => (byte)(i * 3))];
    private static readonly byte[] DataB = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 5))];
    private readonly TempDirectory _dir = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private AppPaths Paths => new(_dir.File("config"), _dir.File("data"));

    private static Catalog TestCatalog() => Catalog.Parse($$"""
        { "version": 1, "entries": [
          { "id": "a", "kind": "whisper", "title": "A", "file": "a.bin", "url": "https://example.com/a.bin",
            "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(DataA))}}", "size": {{DataA.Length}}, "languages": ["*"],
            "licence": { "name": "MIT", "url": "https://example.com" } },
          { "id": "b", "kind": "whisper", "title": "B", "file": "b.bin", "url": "https://example.com/b.bin",
            "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(DataB))}}", "size": {{DataB.Length}}, "languages": ["*"],
            "licence": { "name": "MIT", "url": "https://example.com" } } ] }
        """);

    private DownloadQueue Queue(FakeHttpHandler http, long freeDisk = long.MaxValue) =>
        new(new FileDownloader(http.Client(), [TimeSpan.Zero]), Paths, TestCatalog(), () => freeDisk);

    [Fact]
    public async Task DownloadsQueuedEntriesInOrder()
    {
        var http = new FakeHttpHandler().Respond(_ => Ok(DataA)).Respond(_ => Ok(DataB));
        await using var queue = Queue(http);
        var catalog = TestCatalog();

        queue.Enqueue([catalog.Get("a"), catalog.Get("b")]);
        await queue.WhenIdleAsync(_ct);

        Assert.Equal([DownloadState.Done, DownloadState.Done], queue.Items.Select(i => i.State));
        Assert.Equal(DataB, File.ReadAllBytes(Paths.ResolveModel("b.bin")));
        Assert.Equal("https://example.com/a.bin", http.Requests[0].Request.RequestUri!.ToString());
        Assert.DoesNotContain("\"a\"", File.ReadAllText(Paths.DownloadsFile));
    }

    [Fact(Timeout = 10_000)]
    public async Task PauseKeepsThePartialFileAndResumeContinuesWithARange()
    {
        var stalling = new StallingStream(DataA[..1000]);
        var http = new FakeHttpHandler()
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalling) })
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(DataA[1000..]) });
        await using var queue = Queue(http);
        queue.Enqueue([TestCatalog().Get("a")]);
        await stalling.Drained.WaitAsync(TestContext.Current.CancellationToken);

        queue.Pause();
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DownloadState.Paused, queue.Items[0].State);
        Assert.Equal(1000, new FileInfo(Paths.ResolveModel("a.bin") + ".partial").Length);
        Assert.Contains("\"paused\": true", File.ReadAllText(Paths.DownloadsFile));

        queue.Resume();
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DownloadState.Done, queue.Items[0].State);
        Assert.Equal("bytes=1000-", http.Requests[1].Request.Headers.Range!.ToString());
        Assert.Equal(DataA, File.ReadAllBytes(Paths.ResolveModel("a.bin")));
    }

    [Fact]
    public async Task ARestartedQueueContinuesTheSavedDownloads()
    {
        Directory.CreateDirectory(Paths.DataDirectory);
        File.WriteAllText(Paths.DownloadsFile, """{ "paused": false, "ids": ["b"] }""");
        var http = new FakeHttpHandler().Respond(_ => Ok(DataB));
        await using var queue = Queue(http);

        queue.Restore();
        await queue.WhenIdleAsync(_ct);

        Assert.Equal(DataB, File.ReadAllBytes(Paths.ResolveModel("b.bin")));
    }

    [Fact]
    public async Task AFailedDownloadDoesNotStopTheNextOne()
    {
        var http = new FakeHttpHandler().Respond(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).Respond(_ => Ok(DataB));
        await using var queue = Queue(http);
        var catalog = TestCatalog();

        queue.Enqueue([catalog.Get("a"), catalog.Get("b")]);
        await queue.WhenIdleAsync(_ct);

        Assert.Equal(DownloadState.Failed, queue.Items[0].State);
        Assert.Contains("404", queue.Items[0].Error);
        Assert.Equal(DownloadState.Done, queue.Items[1].State);
    }

    [Fact]
    public async Task NotEnoughSpaceFailsTheEntryWithoutARequest()
    {
        var http = new FakeHttpHandler();
        await using var queue = Queue(http, freeDisk: 1000);

        queue.Enqueue([TestCatalog().Get("a")]);
        await queue.WhenIdleAsync(_ct);

        Assert.Equal(DownloadState.Failed, queue.Items[0].State);
        Assert.Contains("disk space", queue.Items[0].Error);
        Assert.Empty(http.Requests);
    }

    [Fact(Timeout = 10_000)]
    public async Task CancelRemovesTheEntryAndItsPartialFile()
    {
        var stalling = new StallingStream(DataA[..1000]);
        var http = new FakeHttpHandler()
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalling) });
        await using var queue = Queue(http);
        queue.Enqueue([TestCatalog().Get("a")]);
        await stalling.Drained.WaitAsync(TestContext.Current.CancellationToken);

        queue.Cancel("a");
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Empty(queue.Items);
        Assert.False(File.Exists(Paths.ResolveModel("a.bin") + ".partial"));
    }

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    /// <summary>Delivers its bytes, then waits for data that never comes, until the download is cancelled.</summary>
    private sealed class StallingStream(byte[] data) : MemoryStream(data)
    {
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once every byte was read and the stream starts waiting; progress reports are throttled, so tests wait on this.</summary>
        public Task Drained => _drained.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }

            _drained.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
