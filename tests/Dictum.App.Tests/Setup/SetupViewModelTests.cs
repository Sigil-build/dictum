using Dictum.App.Daemon;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.Core.Configuration;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupViewModelTests : IDisposable
{
    private readonly SetupFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private SetupViewModel Open() => new(_fx.Services());

    private static async Task WalkTo(SetupViewModel wizard, int index)
    {
        while (wizard.Index < index)
        {
            Assert.True(wizard.CanNext, $"Next is off on step {wizard.Index + 1}");
            await wizard.NextCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Step 3 with a server that passed its test; the answers otherwise as given.</summary>
    private static async Task UseServer(SetupViewModel wizard, string key = "")
    {
        await WalkTo(wizard, 2);
        wizard.Cleanup.UseServer = true;
        wizard.Cleanup.Model = "qwen2.5:7b";
        wizard.Cleanup.KeyText = key;
        await wizard.Cleanup.TestCommand.ExecuteAsync(null);
    }

    private void Finish(string id) =>
        _fx.Downloads.Set(new QueuedDownload(ModelsPageTests.TestCatalog().Get(id), DownloadState.Done, 0));

    [Fact]
    public void AFreshSetupStartsWithTheUiLanguageAndEnglish()
    {
        var wizard = Open();

        Assert.Equal(("Step 1 of 8 · Languages", "en"), (wizard.StepLine, wizard.Languages.Setting));
        Assert.Equal([true, false, false, false, false, false, false, false], wizard.Bars);
        Assert.Equal(("Next", "Esc closes · settings are kept", false, true), (wizard.NextText, wizard.FootNote, wizard.CanBack, wizard.CanNext));
        Assert.True(wizard.Login.StartAtLogin);
    }

    [Fact]
    public async Task NextAndBackWalkTheSteps()
    {
        var wizard = Open();

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(("Step 2 of 8 · Speech model", true), (wizard.StepLine, wizard.CanBack));
        Assert.Equal([true, true, false, false, false, false, false, false], wizard.Bars);
        Assert.Same(wizard.Model, wizard.Current);

        wizard.BackCommand.Execute(null);
        Assert.Equal(0, wizard.Index);
    }

    [Fact]
    public async Task TheCleanupStepGatesNext()
    {
        var wizard = Open();
        await WalkTo(wizard, 2);

        wizard.Cleanup.UseServer = true;
        Assert.False(wizard.CanNext);

        wizard.Cleanup.Model = "qwen2.5:7b";
        await wizard.Cleanup.TestCommand.ExecuteAsync(null);
        Assert.True(wizard.CanNext);
    }

    [Fact]
    public async Task StepSixListsThePlanAndAsksFirst()
    {
        var wizard = Open();
        await WalkTo(wizard, 5);

        Assert.Equal(("Step 6 of 8 · Downloads", "Download", "Nothing starts until you confirm"), (wizard.StepLine, wizard.NextText, wizard.FootNote));
        Assert.Equal(["ggml-large-v3-turbo-q8_0.bin", "ggml-silero-v6.2.0.bin"], wizard.Downloads.Rows.Select(r => r.File));

        wizard.Downloads.Consent = false;
        Assert.False(wizard.CanNext);
    }

    [Fact]
    public async Task DownloadingQueuesThePlanAndSavesWhenEveryFileIsInPlace()
    {
        var wizard = Open();
        await WalkTo(wizard, 5);

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(("Step 7 of 8 · Downloading", "Downloading…", false), (wizard.StepLine, wizard.NextText, wizard.CanNext));
        Assert.Equal(["download whisper-large-v3-turbo-q8", "download vad-silero-6.2"], _fx.Downloads.Calls);
        Assert.False(_fx.Store.Exists);

        _fx.InstallAll();
        Finish(ModelsPageTests.Turbo);
        Assert.Equal(("Continue", true), (wizard.NextText, wizard.CanNext));
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(("Step 8 of 8 · Ready", "Finish", "Settings → Profiles for everything else"), (wizard.StepLine, wizard.NextText, wizard.FootNote));
        var config = _fx.Store.Load();
        var profile = config.Profiles[0];
        Assert.Equal("en", profile.Language);
        var stt = Assert.IsType<WhisperLocalSttConfig>(config.Stt[profile.Stt]);
        Assert.Equal(("ggml-large-v3-turbo-q8_0.bin", "ggml-silero-v6.2.0.bin"), (stt.Model, stt.VadModel));
        Assert.Equal(["Ctrl", "+", "Alt", "+", "Space"], wizard.Ready.Keys.Select(k => k.Text));
        Assert.StartsWith("Hold these keys and speak.", wizard.Ready.HowTo);
    }

    // Review focus 3.
    [Fact]
    public async Task NothingToDownloadSkipsStepSeven()
    {
        _fx.InstallAll();
        var wizard = Open();
        await WalkTo(wizard, 5);

        Assert.Equal(("Continue", false), (wizard.NextText, wizard.Downloads.HasFiles));
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(7, wizard.Index);
        Assert.True(_fx.Store.Exists);
        Assert.Empty(_fx.Downloads.Calls);
        wizard.BackCommand.Execute(null);
        Assert.Equal(5, wizard.Index);
    }

    [Fact]
    public async Task ATypedKeyIsStoredOnlyAtTheSave()
    {
        _fx.InstallAll();
        var wizard = Open();
        await UseServer(wizard, key: "sk-1");
        await WalkTo(wizard, 5);
        Assert.Empty(_fx.Secrets.Values);

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal("sk-1", _fx.Secrets.Values["dictum-processor-cleanup"]);
        var config = _fx.Store.Load();
        var cleanup = Assert.IsType<LlmProcessorConfig>(config.Processors["cleanup"]);
        Assert.Equal(("http://localhost:8080/v1", "qwen2.5:7b", "secret:dictum-processor-cleanup"), (cleanup.BaseUrl, cleanup.Model, cleanup.ApiKey));
        Assert.Contains("cleanup", config.Profiles[0].Pipeline);
    }

    // Review focus 1.
    [Fact]
    public async Task AKeyStoreFailureStopsTheSave()
    {
        _fx.InstallAll();
        _fx.Secrets.Fails = new InvalidOperationException("No keyring is running.");
        var wizard = Open();
        await UseServer(wizard, key: "sk-1");
        await WalkTo(wizard, 5);

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(("The API key could not be stored: No keyring is running.", 5), (wizard.Error, wizard.Index));
        Assert.False(_fx.Store.Exists);
        Assert.Null(_fx.Autostart.Item);
    }

    // Review focus 1.
    [Fact]
    public async Task AFileMissingAtTheSaveGoesBackToDownloading()
    {
        var wizard = Open();
        await WalkTo(wizard, 5);
        await wizard.NextCommand.ExecuteAsync(null);
        _fx.InstallAll();
        Finish(ModelsPageTests.Turbo);

        File.Delete(_fx.ModelPath(ModelsPageTests.TurboFile));
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal((6, "Some files are not in place any more; they are downloading again."), (wizard.Index, wizard.Error));
        Assert.Equal("download whisper-large-v3-turbo-q8", _fx.Downloads.Calls[^1]);
        Assert.False(_fx.Store.Exists);
    }

    [Fact]
    public async Task StartAtLoginIsWrittenAfterTheSave()
    {
        _fx.InstallAll();
        var wizard = Open();
        await WalkTo(wizard, 5);

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(new LoginItem(LoginApp.DesktopApp, SetupFixture.Executable), _fx.Autostart.Item);
        Assert.Null(wizard.Ready.LoginError);
    }

    // Review focus 3.
    [Fact]
    public async Task ARerunShowsTheCurrentValues()
    {
        _fx.Store.Save(ProfilesPageTests.Sample());
        _fx.Autostart.Installed = LoginApp.DesktopApp;

        var wizard = Open();
        await WalkTo(wizard, 1);

        Assert.Equal("uk, en", wizard.Languages.Setting);
        Assert.Equal(ModelsPageTests.Turbo, wizard.Model.SpeechModel);
        Assert.Equal((true, "http://localhost:11434/v1", "qwen2.5:7b"), (wizard.Cleanup.UseServer, wizard.Cleanup.Url, wizard.Cleanup.Model));
        Assert.Equal("Nexus5\nAvalonia", wizard.Words.Text);
        Assert.True(wizard.Login.StartAtLogin);
    }

    // Review focus 3.
    [Fact]
    public async Task RemovingAWordOnARerunRemovesIt()
    {
        _fx.InstallAll();
        _fx.Store.Save(ProfilesPageTests.Sample());
        var wizard = Open();
        await WalkTo(wizard, 2);
        await wizard.Cleanup.TestCommand.ExecuteAsync(null);
        await WalkTo(wizard, 3);

        wizard.Words.Text = "Nexus5";
        await WalkTo(wizard, 6);

        Assert.Equal(7, wizard.Index);
        Assert.Equal(["Nexus5"], _fx.Store.Load().Profiles[0].Vocabulary);
    }

    [Fact]
    public async Task FinishRaisesFinished()
    {
        _fx.InstallAll();
        var wizard = Open();
        var finished = 0;
        wizard.Finished += () => finished++;
        await WalkTo(wizard, 7);

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task StepEightShowsTheTrayRowUntilTheIconIsKnownToBeVisible()
    {
        _fx.InstallAll();
        var wizard = Open();
        await WalkTo(wizard, 7);

        Assert.Equal((true, true), (wizard.Ready.ShowTrayRow, wizard.Ready.CanOpenTraySettings));
        Assert.StartsWith("Windows hides new tray icons", wizard.Ready.TrayText);
        wizard.Ready.OpenTraySettingsCommand.Execute(null);
        Assert.Equal(1, _fx.TraySettingsOpened);
    }

    // Final review I2: after the save the walk is over, even when the window was closed with ✕ on step 8.
    [Fact]
    public async Task AWalkPastTheSaveCannotResume()
    {
        _fx.InstallAll();
        var wizard = Open();
        Assert.True(wizard.CanResume);

        await WalkTo(wizard, 7);

        Assert.False(wizard.CanResume);
    }

    // Final review I1: a re-run must show the config as it is now; only a walk with files downloading carries on.
    [Fact]
    public async Task AWalkCannotResumeOverConfigEditsUnlessItIsDownloading()
    {
        _fx.Store.Save(ProfilesPageTests.Sample());
        var wizard = Open();
        var edited = ProfilesPageTests.Sample();
        edited.Profiles[0] = edited.Profiles[0] with { Vocabulary = ["Nexus5", "Avalonia", "Kubernetes"] };
        _fx.Store.Save(edited);
        Assert.False(wizard.CanResume);

        var downloading = Open();
        await WalkTo(downloading, 2);
        await downloading.Cleanup.TestCommand.ExecuteAsync(null);
        await WalkTo(downloading, 6);
        _fx.Store.Save(ProfilesPageTests.Sample());

        Assert.True(downloading.CanResume);
    }

    // Final review M6: a wizard closed while its files download asks to come back once they are all in place, to save.
    [Fact]
    public async Task AWizardClosedWhileDownloadingAsksBackWhenTheFilesAreIn()
    {
        var wizard = Open();
        await WalkTo(wizard, 6);
        var asked = 0;
        wizard.FilesReady += () => asked++;
        wizard.OnClosed();

        Finish(ModelsPageTests.Turbo);
        Assert.Equal(0, asked);

        _fx.InstallAll();
        Finish(ModelsPageTests.Turbo);
        Finish(ModelsPageTests.Turbo);

        Assert.Equal(1, asked);
    }

    // Final review M6: on screen, step 7 itself shows the files are in; nothing asks to reopen.
    [Fact]
    public async Task AWizardOnScreenDoesNotAskBack()
    {
        var wizard = Open();
        await WalkTo(wizard, 6);
        var asked = 0;
        wizard.FilesReady += () => asked++;
        wizard.OnClosed();
        wizard.OnOpened();

        _fx.InstallAll();
        Finish(ModelsPageTests.Turbo);

        Assert.Equal(0, asked);
    }

    // PR 5b: the save tells the shell at once, so the daemon starts without waiting for the poll.
    [Fact]
    public async Task TheSaveTellsTheShellAtOnce()
    {
        _fx.InstallAll();
        var wizard = Open();
        var saved = 0;
        wizard.Saved += () =>
        {
            Assert.True(_fx.Store.Exists);
            saved++;
        };

        await WalkTo(wizard, 7);

        Assert.Equal(1, saved);
    }

    // PR 5b: the shell's link and events reach step 8 through the wizard.
    [Fact]
    public async Task StepEightFollowsTheDaemonThroughTheWizard()
    {
        _fx.InstallAll();
        var wizard = Open();
        await WalkTo(wizard, 7);
        Assert.Equal("Starting Dictum…", wizard.Ready.DaemonText);
        var profile = _fx.Store.Load().Profiles[0];

        wizard.OnLink(new DaemonLink(DaemonLinkState.Connected, new DaemonStatus(
            "Idle", null, [new ProfileStatus(profile.Id, profile.Hotkey, "PushToTalk", true)], "hook", "paste", 4242, WarmedUp: true)));
        wizard.OnEvent(new IpcEvent("finished", Outcome: DictationOutcome.Copied));

        Assert.Equal("Dictum is running. Try it in the box below.", wizard.Ready.DaemonText);
        Assert.Equal("Done. The text is on the clipboard: this profile copies instead of pasting.", wizard.Ready.TryResult);
    }
}
