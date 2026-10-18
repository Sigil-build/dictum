using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupDownloadingStepTests : IDisposable
{
    private readonly SetupFixture _fx = new();
    private readonly Catalog _catalog = ModelsPageTests.TestCatalog();

    public SetupDownloadingStepTests() => _fx.Install("ggml-silero-v6.2.0.bin", [1, 2, 3]);

    public void Dispose() => _fx.Dispose();

    private CatalogEntry Turbo => _catalog.Get(ModelsPageTests.Turbo);

    private SetupDownloadingStep Start()
    {
        var step = new SetupDownloadingStep(_fx.Services());
        step.Start([Turbo, _catalog.Get(SetupPlanner.VadId)]);
        step.OnShown();
        return step;
    }

    [Fact]
    public void StartingQueuesOnlyTheMissingFiles()
    {
        var step = Start();

        Assert.Equal(["download whisper-large-v3-turbo-q8"], _fx.Downloads.Calls);
        Assert.Equal(["ggml-large-v3-turbo-q8_0.bin", "ggml-silero-v6.2.0.bin"], step.Files.Select(f => f.Name));
        Assert.Equal("0 MB of 3 MB", step.Files[0].Text);
        Assert.Equal(("0 MB · verified", 100.0), (step.Files[1].Text, step.Files[1].Progress));
        Assert.Equal(("Downloading…", false, "Downloading"), (step.Title, step.CanContinue, step.Name));
    }

    // Review focus 5: the rate and time left come from the queue, and a stall drops them (4d I3).
    [Fact]
    public void ProgressFollowsTheQueueAndAStallDropsTheRate()
    {
        var step = Start();
        _fx.Downloads.Set(new QueuedDownload(Turbo, DownloadState.Downloading, 0));
        _fx.Clock.Advance(TimeSpan.FromSeconds(2));
        _fx.Downloads.Set(new QueuedDownload(Turbo, DownloadState.Downloading, 1_500_000));

        Assert.Equal(("1.5 MB of 3 MB", 50.0), (step.Files[0].Text, step.Files[0].Progress));
        Assert.Equal("1.5 MB of 3 MB · 0.8 MB/s · 2 s left", step.TotalText);

        for (var i = 0; i < 6; i++)
        {
            _fx.Clock.Advance(TimeSpan.FromSeconds(1));
            _fx.Timers.Fire();
        }

        Assert.Equal("1.5 MB of 3 MB", step.TotalText);
    }

    [Fact]
    public void PauseAndResumeGoToTheQueue()
    {
        var step = Start();
        Assert.Equal("Pause", step.PauseText);

        step.PauseOrResumeCommand.Execute(null);

        Assert.Equal(("Download paused", "Resume", "0 MB of 3 MB · paused"), (step.Title, step.PauseText, step.TotalText));
        step.PauseOrResumeCommand.Execute(null);
        Assert.Equal(["download whisper-large-v3-turbo-q8", "pause", "resume"], _fx.Downloads.Calls);
    }

    // Review focus 5.
    [Fact]
    public void AFailedFileOffersTryAgain()
    {
        var step = Start();

        _fx.Downloads.Set(new QueuedDownload(Turbo, DownloadState.Failed, 0, "Not enough disk space."));

        Assert.Equal("Download failed", step.Title);
        Assert.Equal(("Not enough disk space.", true), (step.Files[0].Error, step.Files[0].CanRetry));
        step.Files[0].RetryCommand.Execute(null);
        Assert.Equal("download whisper-large-v3-turbo-q8", _fx.Downloads.Calls[^1]);
    }

    // Review focus 5.
    [Fact]
    public void OnlyTheWizardsFilesCountInItsTotal()
    {
        var step = Start();

        _fx.Downloads.Set(new QueuedDownload(_catalog.Get(ModelsPageTests.Medium), DownloadState.Downloading, 1_000_000));

        Assert.Equal("0 MB of 3 MB", step.TotalText);
        Assert.Equal(0.0001, step.TotalProgress, 3);
    }

    [Fact]
    public void WhenEveryFileIsInPlaceNextOpens()
    {
        var step = Start();

        _fx.Install(ModelsPageTests.TurboFile, ModelsPageTests.TurboData);
        _fx.Downloads.Set(new QueuedDownload(Turbo, DownloadState.Done, Turbo.Size));

        Assert.Equal(("Downloaded and verified", true, true, false), (step.Title, step.IsDone, step.CanContinue, step.ShowPause));
        Assert.Equal(("3 MB · all files verified", "SHA-256 matched for every file."), (step.TotalText, step.Note));
        Assert.Equal(0, _fx.Timers.Pending);
    }
}
