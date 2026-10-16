using Dictum.App.Shell;
using Dictum.Setup;

namespace Dictum.App.Tests.Shell;

public class QueueProgressTests
{
    [Fact]
    public void NothingPendingIsNoProgress() =>
        Assert.Null(QueueProgress.Of([Item("a", 100, 100, DownloadState.Done), Item("b", 50, 0, DownloadState.Failed)]));

    [Fact]
    public void PendingFilesAddUp() =>
        Assert.Equal(0.25, QueueProgress.Of([Item("a", 300, 100, DownloadState.Downloading), Item("b", 100, 0, DownloadState.Queued), Item("c", 10, 10, DownloadState.Done)]));

    [Fact]
    public void APausedFileStillCounts() =>
        Assert.Equal(0.5, QueueProgress.Of([Item("a", 200, 100, DownloadState.Paused)]));

    private static QueuedDownload Item(string id, long size, long bytes, DownloadState state) =>
        new(new CatalogEntry(id, CatalogKind.Whisper, id, $"{id}.bin", "https://example.invalid", "", size, new CatalogLicence("MIT", "https://example.invalid"), ["*"]), state, bytes);
}
