using System.Net;
using System.Security.Cryptography;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Setup;

namespace Dictum.Core.Tests.Setup;

public class SetupRunnerTests : IDisposable
{
    private static readonly byte[] Data = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 3))];
    private readonly TempDirectory _dir = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private AppPaths Paths => new(_dir.File("config"), _dir.File("data"));

    private ConfigStore Store => new(Paths.ConfigFile, NullLog.Instance);

    [Fact]
    public async Task DownloadsMissingFilesThenSavesTheConfig()
    {
        var http = new FakeHttpHandler().Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) });
        var events = new List<SetupEvent>();

        await Runner(http).RunAsync(new SetupPlan(DefaultConfig.Create(), [Entry()]), Store, long.MaxValue, new SyncProgress<SetupEvent>(events.Add), _ct);

        Assert.Equal(Data, File.ReadAllBytes(Paths.ResolveModel("test.bin")));
        Assert.True(Store.Exists);
        Assert.IsType<DownloadStarted>(events[0]);
        Assert.IsType<DownloadFinished>(events[^2]);
        Assert.Equal(new ConfigSaved(Paths.ConfigFile), events[^1]);
    }

    [Fact]
    public async Task NotEnoughSpaceFailsBeforeAnyRequest()
    {
        var http = new FakeHttpHandler();

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            Runner(http).RunAsync(new SetupPlan(DefaultConfig.Create(), [Entry()]), Store, freeDiskBytes: 1000, null, _ct));

        Assert.Contains("disk space", ex.Message);
        Assert.Empty(http.Requests);
        Assert.False(Store.Exists);
    }

    [Fact]
    public async Task FailedDownloadLeavesTheConfigUntouched()
    {
        Store.Save(DefaultConfig.Create() with { Profiles = [] });
        var before = File.ReadAllText(Paths.ConfigFile);
        var http = new FakeHttpHandler().Respond(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<DownloadException>(() =>
            Runner(http).RunAsync(new SetupPlan(DefaultConfig.Create(), [Entry()]), Store, long.MaxValue, null, _ct));

        Assert.Equal(before, File.ReadAllText(Paths.ConfigFile));
    }

    [Fact]
    public async Task InstalledFilesAreSkipped()
    {
        Directory.CreateDirectory(Paths.ModelsDirectory);
        File.WriteAllBytes(Paths.ResolveModel("test.bin"), Data);
        var http = new FakeHttpHandler();

        await Runner(http).DownloadAsync([Entry()], long.MaxValue, null, _ct);

        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task InvalidConfigIsNotSaved()
    {
        var broken = DefaultConfig.Create();
        broken.Profiles[0] = broken.Profiles[0] with { Stt = "missing" };

        await Assert.ThrowsAsync<ConfigException>(() =>
            Runner(new FakeHttpHandler()).RunAsync(new SetupPlan(broken, []), Store, long.MaxValue, null, _ct));

        Assert.False(Store.Exists);
    }

    [Theory]
    [InlineData(874188075L, "874 MB")]
    [InlineData(885098L, "0.9 MB")]
    [InlineData(2489893728L, "2.5 GB")]
    public void FormatsSizesInDecimalUnits(long bytes, string expected) =>
        Assert.Equal(expected, SetupRunner.Format(bytes));

    private SetupRunner Runner(FakeHttpHandler http) =>
        new(new FileDownloader(http.Client(), [TimeSpan.Zero]), Paths);

    private static CatalogEntry Entry() => new(
        "test",
        CatalogKind.Whisper,
        "Test model",
        "test.bin",
        "https://huggingface.co/x/y/resolve/0000000000000000000000000000000000000000/test.bin",
        Convert.ToHexStringLower(SHA256.HashData(Data)),
        Data.Length,
        new CatalogLicence("MIT", "https://example.com"),
        ["*"]);
}
