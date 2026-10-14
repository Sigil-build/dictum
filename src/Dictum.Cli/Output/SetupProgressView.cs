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
            case DownloadProgressed { Progress: var p } when redraw && p.Total > 0:
                var filled = (int)(BarWidth * p.Bytes / p.Total);
                output.Write($"\r  [{new string('#', filled)}{new string(' ', BarWidth - filled)}] {100 * p.Bytes / p.Total,3}%  {SetupRunner.Format(p.Bytes)} of {SetupRunner.Format(p.Total)}   ");
                _lineOpen = true;
                break;
            case DownloadFinished finished:
                EndLine();
                output.WriteLine($"  saved to {finished.Path}");
                break;
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
