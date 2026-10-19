using System.Net;
using System.Security.Cryptography;
using Dictum.Setup;
using Dictum.Testing;

namespace Dictum.Core.Tests.Setup;

public sealed class FileDownloaderTests : IDisposable
{
    private static readonly byte[] Data = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))];
    private readonly TempDirectory _dir = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private string Target => _dir.File("test.bin");

    private string Partial => Target + ".partial";

    [Fact]
    public async Task DownloadsVerifiesAndMovesIntoPlace()
    {
        var http = new FakeHttpHandler().Respond(_ => Ok(Data));
        var reports = new List<DownloadProgress>();

        await Downloader(http).DownloadAsync(Entry(), Target, new SyncProgress<DownloadProgress>(reports.Add), _ct);

        Assert.Equal(Data, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Partial));
        Assert.Equal(new DownloadProgress("test.bin", Data.Length, Data.Length), reports[^1]);
    }

    [Fact]
    public async Task ResumesAPartialFileWithARangeRequest()
    {
        File.WriteAllBytes(Partial, Data[..2000]);
        var http = new FakeHttpHandler().Respond(_ => PartialContent(Data[2000..]));

        await Downloader(http).DownloadAsync(Entry(), Target, null, _ct);

        Assert.Equal("bytes=2000-", http.Requests[0].Request.Headers.Range!.ToString());
        Assert.Equal(Data, File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task StartsOverWhenTheServerIgnoresTheRange()
    {
        File.WriteAllBytes(Partial, Data[..2000]);
        var http = new FakeHttpHandler().Respond(_ => Ok(Data));

        await Downloader(http).DownloadAsync(Entry(), Target, null, _ct);

        Assert.Equal(Data, File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task RetriesAfterAConnectionDropAndResumes()
    {
        var http = new FakeHttpHandler()
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream(Data[..3000])) })
            .Respond(_ => PartialContent(Data[3000..]));

        await Downloader(http).DownloadAsync(Entry(), Target, null, _ct);

        Assert.Equal("bytes=3000-", http.Requests[1].Request.Headers.Range!.ToString());
        Assert.Equal(Data, File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task StalledConnectionIsRetried()
    {
        var http = new FakeHttpHandler()
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Data[..1000])) })
            .Respond(_ => PartialContent(Data[1000..]));
        var downloader = new FileDownloader(http.Client(), [TimeSpan.Zero], stallTimeout: TimeSpan.FromMilliseconds(200));

        await downloader.DownloadAsync(Entry(), Target, null, _ct);

        Assert.Equal(Data, File.ReadAllBytes(Target));
    }

    [Fact(Timeout = 10_000)]
    public async Task ServerThatNeverAnswersIsRetried()
    {
        var http = new FakeHttpHandler()
            .Hang()
            .Respond(_ => Ok(Data));
        var downloader = new FileDownloader(http.Client(), [TimeSpan.Zero], stallTimeout: TimeSpan.FromMilliseconds(200));

        await downloader.DownloadAsync(Entry(), Target, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(Data, File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task WrongHashDeletesTheDownloadAndThrows()
    {
        var http = new FakeHttpHandler().Respond(_ => Ok(Data));

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            Downloader(http).DownloadAsync(Entry(sha256: new string('0', 64)), Target, null, _ct));

        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Partial));
    }

    [Fact]
    public async Task NotFoundFailsWithoutRetrying()
    {
        var http = new FakeHttpHandler().Respond(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<DownloadException>(() => Downloader(http).DownloadAsync(Entry(), Target, null, _ct));

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task NetworkErrorsAfterAllRetriesBecomeADownloadError()
    {
        var http = new FakeHttpHandler()
            .Throw(new HttpRequestException("offline"))
            .Throw(new HttpRequestException("offline"))
            .Throw(new HttpRequestException("offline"))
            .Throw(new HttpRequestException("offline"));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => Downloader(http).DownloadAsync(Entry(), Target, null, _ct));

        Assert.Contains("offline", ex.Message);
        Assert.Equal(4, http.Requests.Count);
    }

    [Fact]
    public async Task CancellingKeepsThePartialFile()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var http = new FakeHttpHandler()
            .Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Data[..3000])) });
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(http).DownloadAsync(Entry(), Target, null, cts.Token));

        Assert.Equal(3000, new FileInfo(Partial).Length);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task MatchesAsyncComparesTheFileWithTheCatalogHash()
    {
        File.WriteAllBytes(Target, Data);
        Assert.True(await FileDownloader.MatchesAsync(Entry(), Target, _ct));

        File.WriteAllBytes(Target, [.. Data[..^1], 0]);
        Assert.False(await FileDownloader.MatchesAsync(Entry(), Target, _ct));
        Assert.True(File.Exists(Target));
    }

    private static FileDownloader Downloader(FakeHttpHandler http) =>
        new(http.Client(), [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

    private static CatalogEntry Entry(string? sha256 = null) => new(
        "test",
        CatalogKind.Whisper,
        "Test model",
        "test.bin",
        "https://huggingface.co/x/y/resolve/0000000000000000000000000000000000000000/test.bin",
        sha256 ?? Convert.ToHexStringLower(SHA256.HashData(Data)),
        Data.Length,
        new CatalogLicence("MIT", "https://example.com"),
        ["*"]);

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static HttpResponseMessage PartialContent(byte[] data) => new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) };

    /// <summary>Delivers its bytes, then fails like a dropped connection.</summary>
    private sealed class DroppingStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("connection reset");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("connection reset");
        }
    }

    /// <summary>Delivers its bytes, then waits for data that never comes.</summary>
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
}
