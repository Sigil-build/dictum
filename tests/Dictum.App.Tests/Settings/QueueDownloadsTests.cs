using System.Net;
using System.Security.Cryptography;
using Dictum.App.Settings;
using Dictum.Core;
using Dictum.Setup;

namespace Dictum.App.Tests.Settings;

public sealed class QueueDownloadsTests : IAsyncDisposable
{
    private static readonly byte[] Data = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))];
    private readonly TempDirectory _dir = new();
    private readonly List<Action> _posted = [];
    private readonly HttpClient _http = new(new Serve());
    private readonly ModelDownloads _queue;
    private readonly QueueDownloads _downloads;
    private readonly CatalogEntry _entry;

    public QueueDownloadsTests()
    {
        var catalog = Catalog.Parse($$"""
            { "version": 1, "entries": [
              { "id": "a", "kind": "whisper", "title": "A", "file": "a.bin", "url": "https://example.com/a.bin",
                "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(Data))}}", "size": {{Data.Length}}, "languages": ["*"],
                "licence": { "name": "MIT", "url": "https://example.com" } } ] }
            """);
        _entry = catalog.Get("a");
        _queue = new ModelDownloads(new FileDownloader(_http, [TimeSpan.Zero]), new AppPaths(_dir.Path, _dir.Path), catalog, () => long.MaxValue);
        _downloads = new QueueDownloads(_queue, action =>
        {
            lock (_posted)
            {
                _posted.Add(action);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        _http.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task TheWorkersChangesArriveThroughThePost()
    {
        var changed = 0;
        _downloads.Changed += () => changed++;

        _downloads.Download(_entry);
        Assert.Equal(1, changed);
        await _queue.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, changed);

        List<Action> posted;
        lock (_posted)
        {
            posted = [.. _posted];
        }

        Assert.NotEmpty(posted);
        posted.ForEach(post => post());
        Assert.Equal(1 + posted.Count, changed);
        Assert.Equal(DownloadState.Done, _downloads.Items.Single().State);
    }

    [Fact]
    public async Task DownloadingIntoAPausedQueueResumesIt()
    {
        _downloads.Pause();

        _downloads.Download(_entry);
        await _queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.False(_downloads.IsPaused);
        Assert.Equal(DownloadState.Done, _downloads.Items.Single().State);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "models", "a.bin")));
    }

    [Fact]
    public void CommandsRaiseChangedAtOnce()
    {
        var changed = 0;
        _downloads.Changed += () => changed++;

        _downloads.Pause();
        _downloads.Cancel("a");
        _downloads.Resume();

        Assert.Equal(3, changed);
    }

    [Fact]
    public void WithoutAQueueNothingHappens()
    {
        IModelDownloads.None.Download(_entry);
        IModelDownloads.None.Pause();

        Assert.Empty(IModelDownloads.None.Items);
        Assert.False(IModelDownloads.None.IsPaused);
    }

    private sealed class Serve : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) });
    }
}
