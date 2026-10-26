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
    private readonly DownloadsLocation _location = new();
    private Func<string, long> _freeAt = _ => 48_000_000_000;

    /// <summary>Follows <see cref="_location"/>, as the shell's paths do.</summary>
    private AppPaths Paths => new AppPaths(_dir.File("config"), _dir.File("data")).WithDownloads(() => _location.Current);

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
            _clock, NullLog.Instance, _location, folder => _freeAt(folder));
        var page = new ModelsPage(session, services);
        page.OnShown();
        return (page, session);
    }

    private void Install(string file, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath(file))!);
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

        Assert.Equal($"Folder: {_dir.File("data")} · 48 GB free · models use 3 MB", page.FolderText);
        Assert.Equal(100.0 * 3_000_003 / (3_000_003 + 48_000_000_000), page.DiskShare, 9);
        Assert.Equal("GPU found: Intel(R) Iris(R) Xe Graphics · 15.8 GB shared · 31.6 GB RAM. Recommendations are based on it.", page.HardwareText);
    }

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

    [Fact]
    public void SpeedComesFromDictationsThenFromScaling()
    {
        Dictated(TurboFile, 5_800, 6_000, 6_200);

        var (page, _) = Open();

        Assert.Equal(("~6 s for 5 s of speech, from your dictations", "Usable"), (Card(page, Turbo).SpeedText, Card(page, Turbo).SpeedBadge));
        // Small: 1.2 × 6.5 / 35 ≈ 0.22, about 1.1 s for 5 s.
        Assert.Equal(("~1.1 s for 5 s of speech (estimate)", "Fast"), (Card(page, Small).SpeedText, Card(page, Small).SpeedBadge));
    }

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
        Assert.EndsWith(" · 48 GB free · models use 0 MB", page.FolderText, StringComparison.Ordinal);
    }

    // The button is off, and a command sent anyway is refused too.
    [Fact]
    public void AModelInUseIsNotDeleted()
    {
        Install(TurboFile, TurboData);
        var (page, _) = Open();

        Card(page, Turbo).DeleteCommand.Execute(null);

        Assert.True(File.Exists(ModelPath(TurboFile)));
        Assert.Equal("Installed", Card(page, Turbo).StateText);
    }

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

    // The daemon runs the saved config, so a file it still uses is not freed by an unsaved edit.
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

    // The daemon says which file it ran; another model's speed is not kept under this one.
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

    // The queue reports nothing while a download stalls; the line must not keep its last rate.
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

    // Models that come as several files live in subfolders; they take space too.
    [Fact]
    public void TheFolderLineNamesTheFolderAndCountsSubfolders()
    {
        Install(TurboFile, TurboData);
        Install(Path.Combine("parakeet", "encoder.onnx"), MediumData);

        var (page, _) = Open();

        Assert.Equal($"Folder: {_dir.File("data")} · 48 GB free · models use 5 MB", page.FolderText);
        Assert.Null(page.FolderMissingText);
        Assert.True(page.CanChangeFolder);
    }

    [Fact]
    public void ChangeIsOffWhileTheQueueHasItems()
    {
        var (page, _) = Open();

        _downloads.Set(new QueuedDownload(Card(page, Small).Entry, DownloadState.Paused, 10));

        Assert.False(page.CanChangeFolder);
        Assert.Equal(Strings.SettingsModelsFolderBusy, page.ChangeFolderHint);

        _downloads.Set(new QueuedDownload(Card(page, Small).Entry, DownloadState.Done, SmallData.Length));

        Assert.True(page.CanChangeFolder);
        Assert.Equal(Strings.SettingsModelsFolderChangeHint, page.ChangeFolderHint);
    }

    [Fact]
    public void ChoosingAnEmptyFolderAppliesItAtOnce()
    {
        var (page, session) = Open();
        var folder = NewFolder();

        page.ChooseFolder(folder);

        Assert.False(page.MoveAsk);
        Assert.Equal(folder, _location.Configured);
        Assert.Equal(folder, session.OnDisk.General.DownloadsDirectory);
        Assert.StartsWith($"Folder: {folder} · ", page.FolderText, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAFolderWithModelsAsksToMove()
    {
        Install(TurboFile, TurboData);
        var (page, session) = Open();
        var folder = NewFolder();

        page.ChooseFolder(folder);

        Assert.True(page.MoveAsk);
        Assert.Equal($"Move 3 MB of models to {folder}?", page.MoveText);
        Assert.Equal($"If you don't move them, they stay in {Path.Combine(_dir.File("data"), "models")} and Dictum no longer sees them.", page.DontMoveNote);
        Assert.Null(session.OnDisk.General.DownloadsDirectory);
        Assert.Null(_location.Current);
        Assert.False(page.CanChangeFolder);
    }

    [Fact]
    public async Task MoveCopiesSwitchesThenDeletes()
    {
        Install(TurboFile, TurboData);
        var old = ModelPath(TurboFile);
        var (page, session) = Open();
        var folder = NewFolder();
        page.ChooseFolder(folder);

        await page.MoveCommand.ExecuteAsync(null);

        Assert.Equal(TurboData, await File.ReadAllBytesAsync(Path.Combine(folder, "models", TurboFile), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(old));
        Assert.Equal(folder, session.OnDisk.General.DownloadsDirectory);
        Assert.Equal(folder, _location.Configured);
        Assert.True(Card(page, Turbo).IsInstalled);
        Assert.Equal((false, false, null), (page.MoveAsk, page.Moving, page.MoveError));
    }

    [Fact]
    public void DontMoveSwitchesAndLeavesTheFiles()
    {
        Install(TurboFile, TurboData);
        var old = ModelPath(TurboFile);
        var (page, session) = Open();
        var folder = NewFolder();
        page.ChooseFolder(folder);

        page.DontMoveCommand.Execute(null);

        Assert.True(File.Exists(old));
        Assert.Equal(folder, session.OnDisk.General.DownloadsDirectory);
        Assert.False(Card(page, Turbo).IsInstalled);
        Assert.False(page.MoveAsk);
    }

    // The free space is read as the copy starts; a Cancel at that point stops it before a byte is copied.
    [Fact]
    public async Task CancelDuringTheMoveKeepsTheOldFolder()
    {
        Install(TurboFile, TurboData);
        var (page, session) = Open();
        var folder = NewFolder();
        page.ChooseFolder(folder);
        _freeAt = _ =>
        {
            Assert.True(page.Moving);
            page.CancelMoveCommand.Execute(null);
            return 48_000_000_000;
        };

        await page.MoveCommand.ExecuteAsync(null);

        Assert.True(File.Exists(ModelPath(TurboFile)));
        Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
        Assert.Null(session.OnDisk.General.DownloadsDirectory);
        Assert.Null(_location.Current);
        Assert.Equal((false, false, null), (page.MoveAsk, page.Moving, page.MoveError));
    }

    [Fact]
    public async Task TooLittleSpaceAtTheTargetSaysSoAndKeepsTheOldFolder()
    {
        Install(TurboFile, TurboData);
        var (page, session) = Open();
        var folder = NewFolder();
        page.ChooseFolder(folder);
        _freeAt = _ => 1;

        await page.MoveCommand.ExecuteAsync(null);

        Assert.StartsWith("The models were not moved: Not enough disk space in ", page.MoveError, StringComparison.Ordinal);
        Assert.EndsWith($"They stay in {Path.Combine(_dir.File("data"), "models")}.", page.MoveError, StringComparison.Ordinal);
        Assert.True(File.Exists(ModelPath(TurboFile)));
        Assert.Null(session.OnDisk.General.DownloadsDirectory);
    }

    [Fact]
    public void TheCurrentFolderOrOneInsideItIsRefused()
    {
        Install(TurboFile, TurboData);
        var (page, _) = Open();

        page.ChooseFolder(Path.Combine(_dir.File("data"), "models", "inner"));

        Assert.NotNull(page.MoveError);
        Assert.False(page.MoveAsk);
        Assert.Null(_location.Current);
    }

    // The lock works both ways: while the page asks or moves, nothing starts a download in the folder it is leaving.
    [Fact]
    public void NoDownloadStartsWhileAMoveIsAsked()
    {
        Install(TurboFile, TurboData);
        var (page, _) = Open();
        page.ChooseFolder(NewFolder());
        var small = Card(page, Small);

        small.DownloadCommand.Execute(null);

        Assert.False(small.ShowDownload || small.ShowDownloadAnyway);
        Assert.Empty(_downloads.Calls);
    }

    // A download started elsewhere (the wizard) while the question was open: Move and Don't move wait for it.
    [Fact]
    public async Task AMoveWaitsForADownloadThatStartedMeanwhile()
    {
        Install(TurboFile, TurboData);
        var (page, session) = Open();
        var folder = NewFolder();
        page.ChooseFolder(folder);
        _downloads.Set(new QueuedDownload(Card(page, Small).Entry, DownloadState.Downloading, 10));

        await page.MoveCommand.ExecuteAsync(null);
        page.DontMoveCommand.Execute(null);

        Assert.Equal(Strings.SettingsModelsFolderBusy, page.MoveError);
        Assert.True(page.MoveAsk);
        Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
        Assert.Null(session.OnDisk.General.DownloadsDirectory);
    }

    // An unplugged drive: the line says why the cards show nothing installed.
    [Fact]
    public void AMissingFolderShowsItsWarning()
    {
        var gone = _dir.File("gone");
        _location.Use(gone);

        var (page, _) = Open();

        Assert.Equal($"The downloads folder {gone} is not available. Connect the drive, or choose another folder.", page.FolderMissingText);
        Assert.False(Card(page, Turbo).IsInstalled);
    }

    // 'dictum models folder' while Settings is open: the watcher moves the location, and the page follows.
    [Fact]
    public void AFolderChangedElsewhereShowsAtOnce()
    {
        var (page, _) = Open();
        var folder = NewFolder();
        Directory.CreateDirectory(Path.Combine(folder, "models"));
        File.WriteAllBytes(Path.Combine(folder, "models", TurboFile), TurboData);

        _location.OnConfig(new DictumConfig { General = new GeneralSettings { DownloadsDirectory = folder } });

        Assert.StartsWith($"Folder: {folder} · ", page.FolderText, StringComparison.Ordinal);
        Assert.True(Card(page, Turbo).IsInstalled);
    }

    private string NewFolder()
    {
        var folder = _dir.File("elsewhere");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
