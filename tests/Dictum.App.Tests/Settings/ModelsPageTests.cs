using System.Security.Cryptography;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Host.Ipc;
using Dictum.Setup;

namespace Dictum.App.Tests.Settings;

public sealed class ModelsPageTests : IDisposable
{
    internal const string Turbo = "whisper-large-v3-turbo-q8";
    internal const string Medium = "whisper-medium-q8";
    internal const string Small = "whisper-small-q8";
    internal const string TurboFile = "ggml-large-v3-turbo-q8_0.bin";
    internal const string SmallFile = "ggml-small-q8_0.bin";
    internal const long Memory = 33_930_241_638;
    internal static readonly byte[] TurboData = Bytes(3_000_000, 3);
    internal static readonly byte[] MediumData = Bytes(2_000_000, 5);
    internal static readonly byte[] SmallData = Bytes(1_000_000, 7);
    internal static readonly byte[] TinyData = Bytes(500_000, 11);
    internal static readonly HardwareInfo IrisXe = new("Intel(R) Iris(R) Xe Graphics", 48_000_000_000, 16_965_120_819, GpuIsIntegrated: true);

    private readonly TempDirectory _dir = new();
    private readonly ManualTimers _timers = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly FakeDownloads _downloads = new();
    private readonly FakeChannel _channel = new();
    private readonly SpeedMeasurements _measurements = new();
    private readonly List<TimeSpan> _waits = [];
    private HardwareInfo _hardware = IrisXe;
    private TaskCompletionSource<HardwareInfo>? _probe;
    private bool _probeFails;

    private AppPaths Paths => new(_dir.File("config"), _dir.File("data"));

    public void Dispose() => _dir.Dispose();

    /// <summary>The built-in catalog's ids and files with small files, so a test can install and hash them.</summary>
    internal static Catalog TestCatalog() => Catalog.Parse($$"""
        { "version": 1, "entries": [
          {{Entry(Turbo, "Whisper large-v3-turbo, 8-bit: best quality, needs a GPU", TurboFile, TurboData, "35", "gpu")}},
          {{Entry(Medium, "Whisper medium, 8-bit: slower on a CPU, more accurate", "ggml-medium-q8_0.bin", MediumData, "19", "cpu")}},
          {{Entry(Small, "Whisper small, 8-bit: fast on a CPU", SmallFile, SmallData, "6.5", "cpu")}},
          {{Entry("whisper-tiny-en", "Whisper tiny, English only: for tests", "ggml-tiny.en.bin", TinyData, "1", "ci", hidden: true, languages: "\"en\"")}},
          { "id": "vad-silero-6.2", "kind": "vad", "title": "Silero voice activity detector", "file": "ggml-silero-v6.2.0.bin",
            "url": "https://huggingface.co/ggml-org/whisper-vad/resolve/abc/ggml-silero-v6.2.0.bin", "sha256": "{{Hash([1, 2, 3])}}", "size": 3,
            "languages": ["*"], "licence": { "name": "MIT", "url": "https://example.com" } } ] }
        """);

    private static string Entry(string id, string title, string file, byte[] data, string cost, string tier, bool hidden = false, string languages = "\"*\"") => $$"""
        { "id": "{{id}}", "kind": "whisper", "title": "{{title}}", "file": "{{file}}",
          "url": "https://huggingface.co/ggerganov/whisper.cpp/resolve/abc/{{file}}", "sha256": "{{Hash(data)}}", "size": {{data.Length}},
          "languages": [{{languages}}], "licence": { "name": "MIT", "url": "https://example.com" },
          "tier": "{{tier}}", "hidden": {{(hidden ? "true" : "false")}}, "relativeCost": {{cost}} }
        """;

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Bytes(int length, int step) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * step))];

    private (ModelsPage Page, SettingsSession Session) Open(DictumConfig? config = null)
    {
        Directory.CreateDirectory(Paths.ConfigDirectory);
        var store = new ConfigStore(Path.Combine(Paths.ConfigDirectory, "config.json"), NullLog.Instance);
        store.Save(config ?? ProfilesPageTests.Sample());
        var session = new SettingsSession(store, _timers, NullLog.Instance);
        session.Load();
        var services = new ModelsServices(Paths, TestCatalog(), _downloads,
            () => _probeFails
                ? Task.FromException<HardwareInfo>(new InvalidOperationException("vkCreateInstance failed"))
                : _probe?.Task ?? Task.FromResult(_hardware),
            () => 48_000_000_000, Memory, 8, _measurements, _channel, _timers,
            wait =>
            {
                _waits.Add(wait);
                return Task.CompletedTask;
            },
            _clock, NullLog.Instance);
        var page = new ModelsPage(session, services);
        page.OnShown();
        return (page, session);
    }

    private void Install(string file, byte[] data)
    {
        Directory.CreateDirectory(Paths.ModelsDirectory);
        File.WriteAllBytes(ModelPath(file), data);
    }

    private string ModelPath(string file) => Path.Combine(Paths.ModelsDirectory, file);

    private void Dictated(string file, params long[] transcribeMs)
    {
        var history = new HistoryStore(Paths.HistoryFile, 200);
        foreach (var ms in transcribeMs)
        {
            history.Append(new HistoryEntry(DateTimeOffset.UnixEpoch, "work", "local", "raw", "text", "uk", 5, Model: file, Backend: "vulkan", TranscribeMs: ms));
        }
    }

    private static ModelCard Card(ModelsPage page, string id) => page.Cards.Single(c => c.Entry.Id == id);

    [Fact]
    public void TheCardsAreTheCatalogsSpeechModels()
    {
        var (page, _) = Open();

        Assert.Equal("Models", page.Title);
        Assert.Equal([Turbo, Medium, Small], page.Cards.Select(c => c.Entry.Id));
        var turbo = Card(page, Turbo);
        Assert.Equal(("Whisper large-v3-turbo, 8-bit", "best quality, needs a GPU"), (turbo.Name, turbo.Note));
        Assert.Equal("3 MB · MIT licence · huggingface.co/ggerganov/whisper.cpp · 99 languages", turbo.Meta);
    }

    [Fact]
    public void AnInstalledHiddenModelGetsACardToo()
    {
        Install("ggml-tiny.en.bin", TinyData);

        var (page, _) = Open();

        Assert.Contains("whisper-tiny-en", page.Cards.Select(c => c.Entry.Id));
    }

    [Fact]
    public void TheHeaderShowsDiskUseAndTheGpuAndMemoryLine()
    {
        Install(TurboFile, TurboData);
        Install("ggml-silero-v6.2.0.bin", [1, 2, 3]);

        var (page, _) = Open();

        Assert.Equal("Models use 3 MB · 48 GB free", page.DiskText);
        Assert.Equal(100.0 * 3_000_003 / (3_000_003 + 48_000_000_000), page.DiskShare, 9);
        Assert.Equal("GPU found: Intel(R) Iris(R) Xe Graphics · 15.8 GB shared · 31.6 GB RAM. Recommendations are based on it.", page.HardwareText);
    }

    // Review focus 5.
    [Fact]
    public async Task BeforeTheProbeAnswersTheLineSaysSo()
    {
        _probe = new TaskCompletionSource<HardwareInfo>();
        var (page, _) = Open();

        Assert.Equal("Looking for a GPU…", page.HardwareText);
        Assert.All(page.Cards, c => Assert.Null(c.Recommendation));
        Assert.All(page.Cards, c => Assert.Null(c.SpeedText));

        _probe.SetResult(new HardwareInfo(null, 0));
        await page.ProbeAsync();

        Assert.Equal("No GPU for Whisper found · 31.6 GB RAM. Recommendations are for the CPU.", page.HardwareText);
        Assert.Equal("Recommended for your computer", Card(page, Small).Recommendation);
        Assert.Equal("Too slow here", Card(page, Turbo).SpeedBadge);
    }

    // Review focus 5.
    [Fact]
    public void AFailedProbeFallsBackToTheCpu()
    {
        _probeFails = true;

        var (page, _) = Open();

        Assert.Equal("No GPU for Whisper found · 31.6 GB RAM. Recommendations are for the CPU.", page.HardwareText);
        Assert.Equal("Recommended for your computer", Card(page, Small).Recommendation);
    }

    [Fact]
    public void TheRecommendationFollowsTheHardware()
    {
        var (page, _) = Open();

        Assert.Equal("Recommended for your GPU", Card(page, Turbo).Recommendation);
        Assert.Null(Card(page, Medium).Recommendation);
        Assert.Null(Card(page, Small).Recommendation);
    }

    // Review focus 2.
    [Fact]
    public void BadgesFollowTheFileAndTheQueue()
    {
        Install(TurboFile, TurboData);
        var (page, _) = Open();

        _downloads.Set(new QueuedDownload(Card(page, Medium).Entry, DownloadState.Paused, 500_000));
        _downloads.Set(new QueuedDownload(Card(page, Small).Entry, DownloadState.Failed, 0, "Not enough disk space."));

        var (turbo, medium, small) = (Card(page, Turbo), Card(page, Medium), Card(page, Small));
        Assert.Equal(("Installed", true, false), (turbo.StateText, turbo.IsInstalled, turbo.ShowDownload));
        Assert.Equal(("Paused", true, true), (medium.StateText, medium.IsPaused, medium.CanCancel));
        Assert.Equal(("0.5 MB of 2 MB", 25.0), (medium.ProgressText, medium.Progress));
        Assert.Equal(("Failed", "Not enough disk space.", (string?)null), (small.StateText, small.DownloadError, small.ProgressText));
        Assert.Equal((true, "Try again · 1 MB"), (small.ShowDownload, small.DownloadText));
    }

    [Fact]
    public void AMissingModelOffersItsDownload()
    {
        var (page, _) = Open();

        var small = Card(page, Small);
        Assert.Equal(("Not installed", true, false, "Download · 1 MB"), (small.StateText, small.ShowDownload, small.ShowDownloadAnyway, small.DownloadText));
        Assert.False(small.IsInstalled || small.IsActive || small.CanCancel);
    }

    // Review focus 2.
    [Fact]
    public void ADownloadShowsItsRateAndTimeLeft()
    {
        var (page, _) = Open();
        var medium = Card(page, Medium);

        _downloads.Set(new QueuedDownload(medium.Entry, DownloadState.Downloading, 0));
        Assert.Equal(("Downloading", true, "0 MB of 2 MB"), (medium.StateText, medium.IsActive, medium.ProgressText));

        _clock.Advance(TimeSpan.FromSeconds(2));
        _downloads.Set(new QueuedDownload(medium.Entry, DownloadState.Downloading, 1_000_000));

        Assert.Equal("1 MB of 2 MB · 0.5 MB/s · 2 s left", medium.ProgressText);
        Assert.Equal(50, medium.Progress);
    }

    [Fact]
    public void QueueChangesShowOnlyWhileThePageIsShown()
    {
        var (page, _) = Open();
        var small = Card(page, Small);

        page.OnHidden();
        _downloads.Set(new QueuedDownload(small.Entry, DownloadState.Downloading, 500_000));
        Assert.Equal("Not installed", small.StateText);

        page.OnShown();
        Assert.Equal("Downloading", small.StateText);
    }

    // Review focus 1.
    [Fact]
    public void UsedByNamesTheProfilesAndKeepsDeleteOff()
    {
        Install(TurboFile, TurboData);
        Install(SmallFile, SmallData);

        var (page, _) = Open();

        var turbo = Card(page, Turbo);
        Assert.Equal(("Used by: Work, Home", false), (turbo.UsedByText, turbo.CanDelete));
        Assert.Equal("In use by Work, Home. Choose another model there first.", turbo.DeleteNote);
        var small = Card(page, Small);
        Assert.Equal(("Not used by any profile", true, (string?)null), (small.UsedByText, small.CanDelete, small.DeleteNote));
        Assert.Equal("Delete · frees 1 MB", small.DeleteText);
    }

    [Fact]
    public void AnAbsolutePathCountsAsTheSameFile()
    {
        Install(SmallFile, SmallData);
        var config = ProfilesPageTests.Sample();
        config.Stt["local"] = new WhisperLocalSttConfig { Model = ModelPath(SmallFile) };

        var (page, _) = Open(config);

        Assert.Equal(("Used by: Work, Home", false), (Card(page, Small).UsedByText, Card(page, Small).CanDelete));
    }

    // Review focus 3.
    [Fact]
    public void SpeedComesFromDictationsThenFromScaling()
    {
        Dictated(TurboFile, 5_800, 6_000, 6_200);

        var (page, _) = Open();

        Assert.Equal(("~6 s for 5 s of speech, from your dictations", "Usable"), (Card(page, Turbo).SpeedText, Card(page, Turbo).SpeedBadge));
        // Small: 1.2 × 6.5 / 35 ≈ 0.22, about 1.1 s for 5 s.
        Assert.Equal(("~1.1 s for 5 s of speech (estimate)", "Fast"), (Card(page, Small).SpeedText, Card(page, Small).SpeedBadge));
    }

    // Review focus 3.
    [Fact]
    public void ATooSlowModelOffersDownloadAnyway()
    {
        _hardware = new HardwareInfo(null, 0);

        var (page, _) = Open();

        var turbo = Card(page, Turbo);
        Assert.Equal(("Too slow here", true), (turbo.SpeedBadge, turbo.IsTooSlow));
        Assert.Equal((false, true, "Download anyway · 3 MB"), (turbo.ShowDownload, turbo.ShowDownloadAnyway, turbo.DownloadText));
        // 0.6 × 35 / 6.5 ≈ 3.2 seconds per second: about 16 s for 5 s.
        Assert.Equal("~16 s for 5 s of speech (estimate)", turbo.SpeedText);
    }

    [Fact]
    public void DownloadPauseResumeAndCancelGoToTheQueue()
    {
        var (page, _) = Open();
        var small = Card(page, Small);

        small.DownloadCommand.Execute(null);
        Assert.Equal(("Waiting", true), (small.StateText, small.IsActive));
        small.PauseCommand.Execute(null);
        small.ResumeCommand.Execute(null);
        small.CancelCommand.Execute(null);

        Assert.Equal(["download whisper-small-q8", "pause", "resume", "cancel whisper-small-q8"], _downloads.Calls);
        Assert.Equal("Not installed", small.StateText);
    }

    [Fact]
    public void AQueueThatCannotSaveShowsWhy()
    {
        var (page, _) = Open();
        _downloads.Throws = new IOException("There is not enough space on the disk.");

        Card(page, Small).DownloadCommand.Execute(null);

        Assert.Equal("Could not update the download list: There is not enough space on the disk.", Card(page, Small).Error);
    }

    [Fact]
    public void DeletingRemovesTheFile()
    {
        Install(SmallFile, SmallData);
        var (page, _) = Open();

        Card(page, Small).DeleteCommand.Execute(null);

        Assert.False(File.Exists(ModelPath(SmallFile)));
        Assert.Equal("Not installed", Card(page, Small).StateText);
        Assert.Equal("Models use 0 MB · 48 GB free", page.DiskText);
    }

    // Review focus 1: the button is off, and a command sent anyway is refused too.
    [Fact]
    public void AModelInUseIsNotDeleted()
    {
        Install(TurboFile, TurboData);
        var (page, _) = Open();

        Card(page, Turbo).DeleteCommand.Execute(null);

        Assert.True(File.Exists(ModelPath(TurboFile)));
        Assert.Equal("Installed", Card(page, Turbo).StateText);
    }

    // Review focus 1.
    [Fact]
    public void ALockedFileShowsWhyItWasNotDeleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file that is open");
        Install(SmallFile, SmallData);
        var (page, _) = Open();

        using (File.Open(ModelPath(SmallFile), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Card(page, Small).DeleteCommand.Execute(null);
        }

        Assert.StartsWith("Could not delete the file: ", Card(page, Small).Error);
        Assert.True(File.Exists(ModelPath(SmallFile)));
    }

    [Fact]
    public async Task VerifyChecksTheHash()
    {
        Install(SmallFile, SmallData);
        var (page, _) = Open();
        var small = Card(page, Small);

        await small.VerifyCommand.ExecuteAsync(null);
        Assert.Equal("SHA-256 matches the catalog.", small.CheckText);

        File.WriteAllBytes(ModelPath(SmallFile), new byte[SmallData.Length]);
        await small.VerifyCommand.ExecuteAsync(null);

        Assert.Equal("SHA-256 does not match the catalog. Delete the file and download it again.", small.CheckText);
        Assert.False(small.Busy);
    }

    // Review focus 4.
    [Fact]
    public void UseInProfileSetsTheProvidersModel()
    {
        Install(TurboFile, TurboData);
        Install(SmallFile, SmallData);
        var (page, session) = Open();
        var small = Card(page, Small);
        Assert.Equal(["Work (also Home)", "Home (also Work)"], small.UseInChoices.Select(c => c.Label));
        Assert.Empty(Card(page, Turbo).UseInChoices);

        small.UseIn = small.UseInChoices[0];

        Assert.Equal(SmallFile, ((WhisperLocalSttConfig)session.Config.Stt["local"]).Model);
        Assert.Equal("Used by: Work, Home", small.UsedByText);
        Assert.Empty(small.UseInChoices);
        Assert.Equal(["Work (also Home)", "Home (also Work)"], Card(page, Turbo).UseInChoices.Select(c => c.Label));
    }

    [Fact]
    public void ProfilesWithARemoteProviderAreNotOffered()
    {
        Install(SmallFile, SmallData);
        var config = ProfilesPageTests.Sample();
        config.Profiles[1] = config.Profiles[1] with { Stt = "remote" };

        var (page, _) = Open(config);

        Assert.Equal(["Work"], Card(page, Small).UseInChoices.Select(c => c.Label));
    }

    [Fact]
    public async Task MeasureRunsTheSampleTwiceAndKeepsTheFaster()
    {
        Install(TurboFile, TurboData);
        var runs = 0;
        _channel.Answer = request => request.Command == IpcCommand.TestStt
            ? new IpcResponse(true, Test: new SttTestResult("тестовий запис", "uk", ++runs == 1 ? 9_000 : 6_884, "vulkan", TurboFile, 6.884))
            : new IpcResponse(true);
        var (page, _) = Open();
        var turbo = Card(page, Turbo);
        Assert.True(turbo.CanMeasure);

        await turbo.MeasureCommand.ExecuteAsync(null);

        Assert.Equal(2, _channel.Sent.Count(r => r is { Command: IpcCommand.TestStt, Stt: "local", Sample: true }));
        Assert.Equal(1.0, _measurements.Ratios[TurboFile], 6);
        Assert.Equal(("~5 s for 5 s of speech, measured", (string?)null, (string?)null), (turbo.SpeedText, turbo.Error, turbo.CheckText));
    }

    [Fact]
    public async Task MeasureWaitsForTheDaemonsReloadAfterASave()
    {
        Install(TurboFile, TurboData);
        Install(SmallFile, SmallData);
        var (page, _) = Open();
        var small = Card(page, Small);
        small.UseIn = small.UseInChoices[0];
        _timers.Fire();
        _clock.Advance(TimeSpan.FromMilliseconds(500));

        await small.MeasureCommand.ExecuteAsync(null);

        Assert.Equal([TimeSpan.FromMilliseconds(1000)], _waits);
    }

    [Fact]
    public async Task MeasureWithoutTheDaemonSaysSo()
    {
        Install(TurboFile, TurboData);
        _channel.Link = new DaemonLink(DaemonLinkState.Starting);
        var (page, _) = Open();
        var turbo = Card(page, Turbo);

        await turbo.MeasureCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SettingsProvidersNoDaemon, turbo.Error);
        Assert.False(turbo.Busy);
        Assert.Empty(_measurements.Ratios);
    }

    [Fact]
    public void AMeasureIsNotOfferedOverDictations()
    {
        Install(TurboFile, TurboData);
        Dictated(TurboFile, 6_000);

        var (page, _) = Open();

        Assert.False(Card(page, Turbo).ShowMeasure);
        Assert.False(Card(page, Turbo).CanMeasure);
    }

    // Final review I1: the daemon runs the saved config, so a file it still uses is not freed by an unsaved edit.
    [Fact]
    public void AModelTheSavedConfigUsesIsNotDeletedBeforeTheSave()
    {
        Install(TurboFile, TurboData);
        Install(SmallFile, SmallData);
        var (page, _) = Open();
        var (turbo, small) = (Card(page, Turbo), Card(page, Small));

        small.UseIn = small.UseInChoices[0];

        Assert.Equal(("Used by: Work, Home", false), (turbo.UsedByText, turbo.CanDelete));
        turbo.DeleteCommand.Execute(null);
        Assert.True(File.Exists(ModelPath(TurboFile)));

        _timers.Fire();
        Assert.Equal(("Not used by any profile", true), (turbo.UsedByText, turbo.CanDelete));
    }

    // Final review I1: an edit that cannot be saved never frees the file the saved config uses.
    [Fact]
    public void AnEditThatCannotBeSavedKeepsTheModelInUse()
    {
        Install(TurboFile, TurboData);
        Install(SmallFile, SmallData);
        var (page, session) = Open();
        session.Edit(c =>
        {
            c.Stt["remote"] = ((OpenAiCompatibleSttConfig)c.Stt["remote"]) with { TimeoutMs = 0 };
            return c;
        });
        var small = Card(page, Small);

        small.UseIn = small.UseInChoices[0];
        _timers.Fire();

        Assert.True(session.HasErrors);
        Assert.False(Card(page, Turbo).CanDelete);
        Card(page, Turbo).DeleteCommand.Execute(null);
        Assert.True(File.Exists(ModelPath(TurboFile)));
    }

    // Final review I2: the daemon says which file it ran; another model's speed is not kept under this one.
    [Fact]
    public async Task AMeasureOfAnotherModelIsNotKept()
    {
        Install(TurboFile, TurboData);
        _channel.Answer = request => request.Command == IpcCommand.TestStt
            ? new IpcResponse(true, Test: new SttTestResult("тестовий запис", "uk", 6_884, "vulkan", SmallFile, 6.884))
            : new IpcResponse(true);
        var (page, _) = Open();
        var turbo = Card(page, Turbo);

        await turbo.MeasureCommand.ExecuteAsync(null);

        Assert.Empty(_measurements.Ratios);
        Assert.Equal("Dictum ran ggml-small-q8_0.bin, not this model. Try again in a moment.", turbo.Error);
        Assert.Equal("~5.5 s for 5 s of speech (estimate)", turbo.SpeedText);
    }

    // Final review I3: the queue reports nothing while a download stalls; the line must not keep its last rate.
    [Fact]
    public void AStalledDownloadStopsShowingARate()
    {
        var (page, _) = Open();
        var medium = Card(page, Medium);
        _downloads.Set(new QueuedDownload(medium.Entry, DownloadState.Downloading, 0));
        _clock.Advance(TimeSpan.FromSeconds(2));
        _downloads.Set(new QueuedDownload(medium.Entry, DownloadState.Downloading, 1_000_000));
        Assert.Equal("1 MB of 2 MB · 0.5 MB/s · 2 s left", medium.ProgressText);

        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _timers.Fire();
        }

        Assert.Equal("1 MB of 2 MB", medium.ProgressText);
        _downloads.Set(new QueuedDownload(medium.Entry, DownloadState.Done, 2_000_000));
        Assert.Equal(0, _timers.Pending);
    }
}
