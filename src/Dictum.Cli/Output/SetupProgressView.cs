using Dictum.Setup;

namespace Dictum.Cli.Output;

/// <summary>
/// Shows setup progress on stderr: one line per download, redrawn in place when stderr is a terminal,
/// and only start and finish lines when it is redirected (CI logs).
/// </summary>
internal sealed class SetupProgressView(TextWriter output, bool redraw) : IProgress<SetupEvent>, IDisposable
{
    private const int BarWidth = 30;
    private bool _lineOpen;

    public void Report(SetupEvent value)
    {
        switch (value)
        {
            case DownloadStarted started:
                output.WriteLine($"Downloading {started.Entry.Title} ({SetupRunner.Format(started.Entry.Size)})");
                break;
            case DownloadProgressed { Progress: var p }:
                Bar(p.Bytes, p.Total);
                break;
            case DownloadFinished finished:
                EndLine();
                output.WriteLine($"  saved to {finished.Path}");
                break;
        }
    }

    /// <summary>The bar alone, redrawn in place, for work that is not a download, such as moving the models.</summary>
    public void Bar(long bytes, long total)
    {
        if (redraw && total > 0)
        {
            var filled = (int)(BarWidth * bytes / total);
            output.Write($"\r  [{new string('#', filled)}{new string(' ', BarWidth - filled)}] {100 * bytes / total,3}%  {SetupRunner.Format(bytes)} of {SetupRunner.Format(total)}   ");
            _lineOpen = true;
        }
    }

    public void Dispose() => EndLine();

    private void EndLine()
    {
        if (_lineOpen)
        {
            output.WriteLine();
            _lineOpen = false;
        }
    }
}
