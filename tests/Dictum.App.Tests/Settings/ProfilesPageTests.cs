using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Settings;

public sealed class ProfilesPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();
    private readonly FakeChannel _channel = new();

    public ProfilesPageTests() => Directory.CreateDirectory(_dir);

    private ConfigStore Store => new(Path.Combine(_dir, "config.json"), NullLog.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static DictumConfig Sample() => new()
    {
        Stt =
        {
            ["local"] = new WhisperLocalSttConfig { Model = "ggml-large-v3-turbo-q8_0.bin" },
            ["remote"] = new OpenAiCompatibleSttConfig { BaseUrl = "http://home-server:9000/v1", Model = "whisper-1" },
        },
        Processors =
        {
            ["cleanup"] = new LlmProcessorConfig { BaseUrl = "http://localhost:11434/v1", Model = "qwen2.5:7b", Prompt = "cleanup" },
            ["terms"] = new ReplaceProcessorConfig { Rules = [new ReplaceRule { From = "кубер", To = "Kubernetes" }] },
            ["filler"] = new ReplaceProcessorConfig(),
        },
        Profiles =
        {
            new ProfileConfig { Id = "work", Name = "Work", Hotkey = "Ctrl+Alt+Space", Stt = "local", Language = "uk, en", Vocabulary = ["Nexus5", "Avalonia"], Pipeline = ["cleanup", "terms"] },
            new ProfileConfig { Id = "home", Name = "Home", Hotkey = "Ctrl+Alt+H", Stt = "local", Language = "uk" },
        },
    };

    private (ProfilesPage Page, SettingsSession Session) Open(DictumConfig? config = null)
    {
        Store.Save(config ?? Sample());
        var session = new SettingsSession(Store, _timers, NullLog.Instance);
        session.Load();
        return (new ProfilesPage(session, _channel, NullLog.Instance), session);
    }

    private static ProfileConfig Saved(SettingsSession session, string id) => session.Config.Profiles.Single(p => p.Id == id);

    private static DaemonLink Linked(bool paused, bool registered) => new(DaemonLinkState.Connected,
        new DaemonStatus("Idle", null, [new ProfileStatus("work", "Ctrl+Alt+Space", "PushToTalk", registered)], "hook", "paste", 4242, Paused: paused));

    [Fact]
    public void TheListShowsNamesWithLanguagesAndHotkeys()
    {
        var (page, _) = Open();

        Assert.Equal(["Work", "Home"], page.Profiles.Select(p => p.Name));
        Assert.Equal(["uk, en · Ctrl+Alt+Space", "uk · Ctrl+Alt+H"], page.Profiles.Select(p => p.Caption));
        Assert.Equal("work", page.Selected?.Id);
        Assert.Equal(("Work", "Ctrl+Alt+Space"), (page.Name, page.Hotkey));
        Assert.True(page.CanDelete);
    }

    [Fact]
    public void SelectingAProfileLoadsItsEditor()
    {
        var (page, _) = Open();

        page.Selected = page.Profiles[1];

        Assert.Equal(("Home", "Ctrl+Alt+H"), (page.Name, page.Hotkey));
        Assert.Equal(LanguageMode.One, page.LanguagesMode?.Value);
        Assert.Equal("uk", page.SingleLanguage?.Value);
        Assert.True(page.ChainEmpty);
    }

    [Fact]
    public void ANewProfileCopiesProviderAndLanguagesAndIsSelected()
    {
        var (page, session) = Open();

        page.NewProfileCommand.Execute(null);

        Assert.Equal(3, page.Profiles.Count);
        Assert.Equal("profile", page.Selected?.Id);
        Assert.Equal(Strings.SettingsProfilesNewName, page.Name);
        var added = Saved(session, "profile");
        Assert.Equal(("local", "uk, en", (string?)null), (added.Stt, added.Language, added.Hotkey));
        Assert.False(session.HasErrors);
    }

    [Fact]
    public void DeleteRemovesTheProfileButNeverTheLast()
    {
        var (page, session) = Open();

        page.DeleteProfileCommand.Execute(null);
        Assert.Equal(["home"], session.Config.Profiles.Select(p => p.Id));
        Assert.Equal("home", page.Selected?.Id);
        Assert.False(page.CanDelete);

        page.DeleteProfileCommand.Execute(null);
        Assert.Single(session.Config.Profiles);
    }

    [Fact]
    public void AnEmptyNameFallsBackToTheId()
    {
        var (page, session) = Open();

        page.Name = "";

        Assert.Null(Saved(session, "work").Name);
        Assert.Equal("work", page.NamePlaceholder);
        Assert.Equal("work", page.Profiles[0].Name);
    }

    [Fact]
    public void ARecordedHotkeyIsSavedAndADuplicateIsShown()
    {
        var (page, session) = Open();

        page.Hotkey = "Ctrl+Alt+K";
        Assert.Equal("Ctrl+Alt+K", Saved(session, "work").Hotkey);

        page.Hotkey = "Ctrl+Alt+H";
        Assert.Equal("Ctrl+Alt+H is already used by another profile", page.HotkeyError);
    }

    // Review focus 1.
    [Fact]
    public void RecordingPausesAndResumesTheDaemonsHotkeys()
    {
        var (page, _) = Open();

        page.IsRecording = true;
        Assert.Equal(["pause-hotkeys"], _channel.Commands);

        page.IsRecording = false;
        Assert.Equal(["pause-hotkeys", "resume-hotkeys"], _channel.Commands);
    }

    // Review focus 1.
    [Fact]
    public void RecordingLeavesTheUsersOwnPauseAlone()
    {
        _channel.Link = Linked(paused: true, registered: false);
        var (page, _) = Open();

        page.IsRecording = true;
        page.IsRecording = false;

        Assert.Empty(_channel.Commands);
    }

    // Review focus 1.
    [Fact]
    public void ALeftPageEndsRecording()
    {
        var (page, _) = Open();
        page.IsRecording = true;

        page.OnHidden();

        Assert.False(page.IsRecording);
        Assert.Equal("resume-hotkeys", _channel.Commands.Last());
    }

    [Fact]
    public void ResumeWaitsForThePausesAnswer()
    {
        var pause = new TaskCompletionSource<IpcResponse>();
        _channel.Held["pause-hotkeys"] = pause;
        var (page, _) = Open();

        page.IsRecording = true;
        page.IsRecording = false;
        Assert.Equal(["pause-hotkeys"], _channel.Commands);

        pause.SetResult(new IpcResponse(true));
        Assert.Equal(["pause-hotkeys", "resume-hotkeys"], _channel.Commands);
    }

    [Fact]
    public void AHotkeyTheDaemonCouldNotRegisterIsWarnedUnlessPaused()
    {
        var (page, _) = Open();

        page.OnLink(Linked(paused: false, registered: false));
        Assert.Equal(Strings.SettingsProfilesNotRegistered, page.HotkeyWarning);

        page.OnLink(Linked(paused: true, registered: false));
        Assert.Null(page.HotkeyWarning);
    }

    [Fact]
    public void LanguageModesWriteTheSetting()
    {
        var (page, session) = Open();

        page.LanguagesMode = page.LanguageModes.Single(m => m.Value == LanguageMode.One);
        Assert.Equal("uk", Saved(session, "work").Language);

        page.LanguagesMode = page.LanguageModes.Single(m => m.Value == LanguageMode.Auto);
        Assert.Equal("auto", Saved(session, "work").Language);
        Assert.True(page.ShowsAuto);

        page.LanguagesMode = page.LanguageModes.Single(m => m.Value == LanguageMode.Several);
        Assert.Equal("en", Saved(session, "work").Language);
        Assert.Equal(LanguageMode.Several, page.LanguagesMode?.Value);
    }

    [Fact]
    public void SeveralStaysSeveralWhenOneChipIsLeft()
    {
        var (page, session) = Open();

        page.Languages.Single(l => l.Code == "en").Remove.Execute(null);

        Assert.Equal("uk", Saved(session, "work").Language);
        Assert.Equal(LanguageMode.Several, page.LanguagesMode?.Value);
        Assert.False(page.Languages.Single().Remove.CanExecute(null));
    }

    [Fact]
    public void AddingALanguageAppendsItAndClearsThePicker()
    {
        var (page, session) = Open();

        page.LanguageToAdd = page.AllLanguages.Single(l => l.Value == "pl");

        Assert.Equal("uk, en, pl", Saved(session, "work").Language);
        Assert.Null(page.LanguageToAdd);
    }

    [Fact]
    public void SimilarLanguagesWarnWithTheirOwnNames()
    {
        var (page, _) = Open();

        page.LanguageToAdd = page.AllLanguages.Single(l => l.Value == "be");

        Assert.Equal("Українська and Беларуская are closely related — Whisper often picks the wrong one. Keep one here and move the other to its own profile.", page.SimilarWarning);
        Assert.Equal([true, false, true], page.Languages.Select(l => l.Warned));
    }

    // Review focus 4.
    [Fact]
    public void TypingACommaAddsTheWord()
    {
        var (page, session) = Open();

        Assert.Equal("", page.AddWords("KDE,"));
        Assert.Equal(["Nexus5", "Avalonia", "KDE"], Saved(session, "work").Vocabulary);

        Assert.Equal("pull re", page.AddWords("Wayland\nydotool; nexus5, pull re"));
        Assert.Equal(["Nexus5", "Avalonia", "KDE", "Wayland", "ydotool"], Saved(session, "work").Vocabulary);
        Assert.Equal("Words: 5", page.WordCount);
    }

    [Fact]
    public void AWordChipRemovesItsWord()
    {
        var (page, session) = Open();

        page.Words[0].Remove.Execute(null);

        Assert.Equal(["Avalonia"], Saved(session, "work").Vocabulary);
    }

    [Fact]
    public void TheChainShowsKindsAndMoves()
    {
        var (page, session) = Open();

        Assert.Equal(["LLM prompt", "Rules: 1"], page.Chain.Select(s => s.Kind));
        Assert.Equal([1, 2], page.Chain.Select(s => s.Position));

        page.MoveStep(0, 1);
        Assert.Equal(["terms", "cleanup"], Saved(session, "work").Pipeline);

        page.MoveStep(1, 5);
        Assert.Equal(["terms", "cleanup"], Saved(session, "work").Pipeline);
    }

    [Fact]
    public void StepsCanBeRemovedAndAdded()
    {
        var (page, session) = Open();

        page.Chain[0].Remove.Execute(null);
        Assert.Equal(["terms"], Saved(session, "work").Pipeline);
        Assert.Equal(["cleanup", "filler"], page.StepsToAdd.Select(s => s.Value));

        page.StepToAdd = page.StepsToAdd[0];
        Assert.Equal(["terms", "cleanup"], Saved(session, "work").Pipeline);
        Assert.Null(page.StepToAdd);
    }

    [Fact]
    public void OutputAndPasteShortcutAreSaved()
    {
        var (page, session) = Open();

        page.OutputType = true;
        Assert.Equal(OutputMode.Type, Saved(session, "work").Output);
        Assert.False(page.OutputPaste);

        page.OutputPaste = true;
        page.Shortcut = page.PasteShortcuts.Single(s => s.Value == PasteShortcut.CtrlShiftV);
        Assert.Equal((OutputMode.Paste, PasteShortcut.CtrlShiftV), (Saved(session, "work").Output, Saved(session, "work").PasteShortcut));
    }

    [Fact]
    public void ProvidersAreNamedAndChosen()
    {
        var (page, session) = Open();

        Assert.Equal(["Local Whisper · large-v3-turbo-q8_0", "Remote · home-server:9000"], page.Providers.Select(p => p.Label));

        page.Provider = page.Providers[1];

        Assert.Equal("remote", Saved(session, "work").Stt);
        Assert.Equal("'remote' takes a single language, so with a list the server detects any language", page.LanguageNote);
    }

    [Fact]
    public void TheSpeechHintIsSaved()
    {
        var (page, session) = Open();

        page.SpeechHint = "Розмова про код.";
        Assert.Equal("Розмова про код.", Saved(session, "work").SttPrompt);

        page.SpeechHint = "";
        Assert.Null(Saved(session, "work").SttPrompt);
    }

    [Fact]
    public void AnOutsideEditKeepsTheSelectedProfile()
    {
        var (page, session) = Open();
        page.Selected = page.Profiles[1];
        var reordered = Sample();
        reordered.Profiles.Reverse();

        session.OnFileChanged(reordered);

        Assert.Equal("home", page.Selected?.Id);
        Assert.Equal(["Home", "Work"], page.Profiles.Select(p => p.Name));
    }
}
