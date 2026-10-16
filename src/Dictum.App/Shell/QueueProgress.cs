using Dictum.Setup;

namespace Dictum.App.Shell;

public static class QueueProgress
{
    /// <summary>The share of bytes done over the files still to finish (queued, downloading, paused); null when there are none.</summary>
    public static double? Of(IReadOnlyList<QueuedDownload> items)
    {
        var pending = items.Where(i => i.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused).ToList();
        var total = pending.Sum(i => i.Entry.Size);
        return total == 0 ? null : (double)pending.Sum(i => Math.Min(i.Bytes, i.Entry.Size)) / total;
    }
}
