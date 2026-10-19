using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Host.Ipc;

namespace Dictum.App.ViewModels.Settings;

public sealed record ProfileItem(string Id, string Name, string Caption)
{
    public override string ToString() => Name;
}

public sealed record LanguageChip(string Code, string Name, bool Warned, ICommand Remove);

public sealed record WordChip(string Text, ICommand Remove);

public sealed record ChainStep(string Id, string Kind, int Position, ICommand Remove);

/// <summary>
/// Settings → Profiles: the profile list and the editor of the selected one. Every edit finds the profile by id inside
/// the change, as an outside edit may reorder the list. While the recorder listens, the daemon's hotkeys are paused
/// through a request queue, unless they were paused already.
/// </summary>
public sealed partial class ProfilesPage : SettingsPage
{
    private readonly SettingsSession _session;
    private readonly IDaemonChannel _channel;
    private readonly RequestQueue _requests = new();
    private string? _id;
    private LanguageMode? _shownMode;
    private bool _pausedByUs;
    private bool _loading;

    public ProfilesPage(SettingsSession session, IDaemonChannel channel)
    {
        _session = session;
        _channel = channel;
        Modes = [new(HotkeyMode.PushToTalk, Strings.SettingsProfilesPushToTalk), new(HotkeyMode.Toggle, Strings.SettingsProfilesToggle)];
        LanguageModes =
        [
            new(LanguageMode.One, Strings.SettingsProfilesLanguageOne),
            new(LanguageMode.Several, Strings.SettingsProfilesLanguageSeveral),
            new(LanguageMode.Auto, Strings.SettingsProfilesLanguageAuto),
        ];
        AllLanguages = [.. WhisperLanguages.Codes.Select(code => new Option<string>(code, WhisperLanguages.Name(code)))
            .OrderBy(o => o.Label, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))];
        PasteShortcuts =
        [
            new(PasteShortcut.Auto, Strings.SettingsProfilesShortcutAuto),
            new(PasteShortcut.CtrlV, "Ctrl+V"),
            new(PasteShortcut.CtrlShiftV, "Ctrl+Shift+V"),
        ];
        session.Changed += Load;
        Load();
    }

    public override string Title => Strings.SettingsProfiles;

    public IReadOnlyList<Option<HotkeyMode>> Modes { get; }

    public IReadOnlyList<Option<LanguageMode>> LanguageModes { get; }

    public IReadOnlyList<Option<string>> AllLanguages { get; }

    public IReadOnlyList<Option<PasteShortcut>> PasteShortcuts { get; }

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ProfileItem> Profiles { get; private set; } = [];

    [ObservableProperty]
    public partial ProfileItem? Selected { get; set; }

    [ObservableProperty]
    public partial bool HasProfile { get; private set; }

    [ObservableProperty]
    public partial bool CanDelete { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string NamePlaceholder { get; private set; } = "";

    [ObservableProperty]
    public partial string? Hotkey { get; set; }

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string? HotkeyError { get; private set; }

    [ObservableProperty]
    public partial string? HotkeyWarning { get; private set; }

    [ObservableProperty]
    public partial Option<HotkeyMode>? Mode { get; set; }

    [ObservableProperty]
    public partial Option<LanguageMode>? LanguagesMode { get; set; }

    [ObservableProperty]
    public partial bool ShowsOneLanguage { get; private set; }

    [ObservableProperty]
    public partial bool ShowsSeveralLanguages { get; private set; }

    [ObservableProperty]
    public partial bool ShowsAuto { get; private set; }

    [ObservableProperty]
    public partial Option<string>? SingleLanguage { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<LanguageChip> Languages { get; private set; } = [];

    [ObservableProperty]
    public partial Option<string>? LanguageToAdd { get; set; }

    [ObservableProperty]
    public partial string? SimilarWarning { get; private set; }

    /// <summary>A validator warning on the languages other than the similar pair, such as a list sent to a remote server.</summary>
    [ObservableProperty]
    public partial string? LanguageNote { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<WordChip> Words { get; private set; } = [];

    [ObservableProperty]
    public partial string WordCount { get; private set; } = "";

    [ObservableProperty]
    public partial string? WordsNote { get; private set; }

    [ObservableProperty]
    public partial string SpeechHint { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<ChainStep> Chain { get; private set; } = [];

    [ObservableProperty]
    public partial bool ChainEmpty { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> StepsToAdd { get; private set; } = [];

    [ObservableProperty]
    public partial Option<string>? StepToAdd { get; set; }

    [ObservableProperty]
    public partial bool CanAddStep { get; private set; }

    [ObservableProperty]
    public partial string? ChainNote { get; private set; }

    [ObservableProperty]
    public partial bool OutputPaste { get; set; }

    [ObservableProperty]
    public partial bool OutputType { get; set; }

    [ObservableProperty]
    public partial bool OutputClipboard { get; set; }

    [ObservableProperty]
    public partial Option<PasteShortcut>? Shortcut { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> Providers { get; private set; } = [];

    [ObservableProperty]
    public partial Option<string>? Provider { get; set; }

    [ObservableProperty]
    public partial string? ProviderError { get; private set; }

    private ProfileConfig? Current => _session.Config.Profiles.FirstOrDefault(p => p.Id == _id);

    public override void OnHidden() => IsRecording = false;

    /// <summary>
    /// Text from the words box: complete words are added, and the part still being typed comes back for the box. The
    /// view owns the box, since a reset made inside the box's own binding write never reaches it.
    /// </summary>
    public string AddWords(string text)
    {
        var (words, typing) = WordList.Split(text);
        if (words.Count == 0)
        {
            return text;
        }

        EditProfile(p => p with { Vocabulary = WordList.Add(p.Vocabulary, words) });
        return typing;
    }

    public override void OnLink(DaemonLink link)
    {
        // The daemon can outlive a dropped event stream, still paused by us: lift the pause once it answers again.
        // A daemon that was restarted instead starts unpaused, and the resume does nothing.
        if (link.State == DaemonLinkState.Connected && _pausedByUs && !IsRecording)
        {
            ResumeHotkeys();
        }

        HotkeyWarning = RegistrationWarning(link);
    }

    /// <summary>Moves a chain step; out-of-range positions change nothing.</summary>
    public void MoveStep(int from, int to) => EditProfile(p =>
    {
        if (from == to || from < 0 || to < 0 || from >= p.Pipeline.Count || to >= p.Pipeline.Count)
        {
            return p;
        }

        var steps = p.Pipeline.ToList();
        var step = steps[from];
        steps.RemoveAt(from);
        steps.Insert(to, step);
        return p with { Pipeline = steps };
    });

    partial void OnSelectedChanged(ProfileItem? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        IsRecording = false;
        _id = value.Id;
        _shownMode = null;
        Reload();
    }

    partial void OnNameChanged(string value) => EditProfile(p => p with { Name = string.IsNullOrWhiteSpace(value) ? null : value });

    partial void OnHotkeyChanged(string? value) => EditProfile(p => p with { Hotkey = value });

    partial void OnIsRecordingChanged(bool value)
    {
        if (value)
        {
            if (!_pausedByUs && _channel.Link is { State: DaemonLinkState.Connected, Status.Paused: false })
            {
                _pausedByUs = true;
                Send(IpcCommand.PauseHotkeys);
            }
        }
        else if (_pausedByUs && _channel.Link.State == DaemonLinkState.Connected)
        {
            ResumeHotkeys();
        }
    }

    partial void OnModeChanged(Option<HotkeyMode>? value)
    {
        if (value is not null)
        {
            EditProfile(p => p with { Mode = value.Value });
        }
    }

    partial void OnLanguagesModeChanged(Option<LanguageMode>? value)
    {
        if (value is not null)
        {
            EditLanguage(s => s.WithMode(value.Value, WhisperLanguages.Fallback()));
        }
    }

    partial void OnSingleLanguageChanged(Option<string>? value)
    {
        if (value is not null)
        {
            EditLanguage(s => s.With(value.Value));
        }
    }

    partial void OnLanguageToAddChanged(Option<string>? value)
    {
        if (value is not null)
        {
            EditLanguage(s => s.With(value.Value));
        }
    }

    partial void OnSpeechHintChanged(string value) => EditProfile(p => p with { SttPrompt = value.Length == 0 ? null : value });

    partial void OnStepToAddChanged(Option<string>? value)
    {
        if (value is not null)
        {
            EditProfile(p => p with { Pipeline = [.. p.Pipeline, value.Value] });
        }
    }

    partial void OnOutputPasteChanged(bool value) => SetOutput(value, OutputMode.Paste);

    partial void OnOutputTypeChanged(bool value) => SetOutput(value, OutputMode.Type);

    partial void OnOutputClipboardChanged(bool value) => SetOutput(value, OutputMode.Clipboard);

    partial void OnShortcutChanged(Option<PasteShortcut>? value)
    {
        if (value is not null)
        {
            EditProfile(p => p with { PasteShortcut = value.Value });
        }
    }

    partial void OnProviderChanged(Option<string>? value)
    {
        if (value is not null)
        {
            EditProfile(p => p with { Stt = value.Value });
        }
    }

    [RelayCommand]
    private void NewProfile()
    {
        if (!CanEdit)
        {
            return;
        }

        var source = Current;
        var config = _session.Config;
        var id = UniqueIds.For("profile", candidate => config.Profiles.Any(p => p.Id == candidate));
        IsRecording = false;
        _id = id;
        _shownMode = null;
        _session.Edit(c =>
        {
            c.Profiles.Add(new ProfileConfig
            {
                Id = id,
                Name = Strings.SettingsProfilesNewName,
                Stt = source?.Stt ?? c.Stt.Keys.FirstOrDefault() ?? DefaultConfig.LocalSttId,
                Language = source?.Language,
            });
            return c;
        });
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (!CanEdit || _id is not { } id || _session.Config.Profiles.Count <= 1)
        {
            return;
        }

        IsRecording = false;
        _id = null;
        _session.Edit(c =>
        {
            c.Profiles.RemoveAll(p => p.Id == id);
            return c;
        });
    }

    private void SetOutput(bool chosen, OutputMode output)
    {
        if (chosen)
        {
            EditProfile(p => p with { Output = output });
        }
    }

    private void EditLanguage(Func<LanguageSetting, LanguageSetting> change)
    {
        if (_loading)
        {
            return;
        }

        var next = change(LanguageSetting.Parse(Current?.Language, _shownMode));
        _shownMode = next.Mode;
        EditProfile(p => p with { Language = next.Format() });
    }

    private void EditProfile(Func<ProfileConfig, ProfileConfig> change)
    {
        if (_loading || _id is not { } id)
        {
            return;
        }

        _session.Edit(c =>
        {
            var index = c.Profiles.FindIndex(p => p.Id == id);
            if (index >= 0)
            {
                c.Profiles[index] = change(c.Profiles[index]);
            }

            return c;
        });
    }

    private void ResumeHotkeys()
    {
        _pausedByUs = false;
        Send(IpcCommand.ResumeHotkeys);
    }

    /// <summary>Pause and resume go one after another, so the daemon ends in the state sent last.</summary>
    private void Send(string command) => _requests.Enqueue(() => _channel.TrySendAsync(new IpcRequest(command), CancellationToken.None));

    private string? RegistrationWarning(DaemonLink link) =>
        link is { State: DaemonLinkState.Connected, Status: { Paused: false } status }
        && status.Profiles.FirstOrDefault(p => p.Id == _id) is { Registered: false, Hotkey: not null }
            ? Strings.SettingsProfilesNotRegistered
            : null;

    private void Load()
    {
        _loading = true;
        try
        {
            var config = _session.Config;
            CanEdit = _session.CanEdit;
            List<ProfileItem> items = [.. config.Profiles.Select(p => new ProfileItem(p.Id, p.Name ?? p.Id, Caption(p)))];
            if (!items.SequenceEqual(Profiles))
            {
                Profiles = items;
            }

            Selected = Profiles.FirstOrDefault(p => p.Id == _id) ?? (Profiles.Count > 0 ? Profiles[0] : null);
            _id = Selected?.Id;
            CanDelete = CanEdit && Profiles.Count > 1;
            LoadEditor(config);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            LoadEditor(_session.Config);
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadEditor(DictumConfig config)
    {
        var index = config.Profiles.FindIndex(p => p.Id == _id);
        HasProfile = index >= 0;
        if (index < 0)
        {
            return;
        }

        var profile = config.Profiles[index];
        var path = ConfigPaths.Profile(index);
        Name = profile.Name ?? "";
        NamePlaceholder = profile.Id;
        Hotkey = profile.Hotkey;
        HotkeyError = _session.IssueAt(ConfigPaths.Field(path, nameof(ProfileConfig.Hotkey))) ?? DuplicateHotkey(config, index);
        HotkeyWarning = RegistrationWarning(_channel.Link);
        Mode = Modes.FirstOrDefault(m => m.Value == profile.Mode) ?? Modes[0];
        LoadLanguages(profile, path);
        LoadWords(profile, path);
        SpeechHint = profile.SttPrompt ?? "";
        LoadChain(config, profile, path);
        LoadOutput(profile);
        LoadProvider(config, profile, path);
    }

    private void LoadLanguages(ProfileConfig profile, string path)
    {
        var setting = LanguageSetting.Parse(profile.Language, _shownMode);
        LanguagesMode = LanguageModes.Single(m => m.Value == setting.Mode);
        ShowsOneLanguage = setting.Mode == LanguageMode.One;
        ShowsSeveralLanguages = setting.Mode == LanguageMode.Several;
        ShowsAuto = setting.Mode == LanguageMode.Auto;
        var pairs = LanguageList.SimilarPairs(setting.Codes);
        var warned = pairs.SelectMany(p => new[] { p.First, p.Second }).ToHashSet();
        var removable = setting.Codes.Count > 1;
        Languages = [.. setting.Codes.Select(code => new LanguageChip(code, WhisperLanguages.Name(code), warned.Contains(code),
            new RelayCommand(() => EditLanguage(s => s.Without(code)), () => removable)))];
        SingleLanguage = ShowsOneLanguage ? AllLanguages.FirstOrDefault(o => o.Value == setting.Codes[0]) : null;
        LanguageToAdd = null;
        SimilarWarning = pairs.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsProfilesSimilar, WhisperLanguages.Name(pairs[0].First), WhisperLanguages.Name(pairs[0].Second))
            : null;
        var similarText = LanguageList.SimilarWarning(setting.Codes);
        LanguageNote = _session.Issues.FirstOrDefault(i => i.Path == ConfigPaths.Field(path, nameof(ProfileConfig.Language)) && i.Message != similarText)?.Message;
    }

    private void LoadWords(ProfileConfig profile, string path)
    {
        Words = [.. profile.Vocabulary.Select(word => new WordChip(word,
            new RelayCommand(() => EditProfile(p => p with { Vocabulary = [.. p.Vocabulary.Where(w => w != word)] }))))];
        WordCount = string.Format(CultureInfo.CurrentCulture, Strings.SettingsProfilesWordCount, profile.Vocabulary.Count);
        WordsNote = _session.Issues.FirstOrDefault(i => i.Path.StartsWith(ConfigPaths.Field(path, nameof(ProfileConfig.Vocabulary)), StringComparison.Ordinal))?.Message;
    }

    private void LoadChain(DictumConfig config, ProfileConfig profile, string path)
    {
        Chain = [.. profile.Pipeline.Select((id, i) => new ChainStep(id, Kind(config, id), i + 1,
            new RelayCommand(() => EditProfile(p => p with { Pipeline = [.. p.Pipeline.Where(s => s != id)] }))))];
        ChainEmpty = Chain.Count == 0;
        StepsToAdd = [.. config.Processors.Keys.Where(id => !profile.Pipeline.Contains(id)).Order(StringComparer.Ordinal)
            .Select(id => new Option<string>(id, string.Format(CultureInfo.CurrentCulture, Strings.SettingsProfilesStep, id, Kind(config, id))))];
        StepToAdd = null;
        CanAddStep = StepsToAdd.Count > 0;
        ChainNote = _session.IssueAt(ConfigPaths.Field(path, nameof(ProfileConfig.Pipeline)));
    }

    private void LoadOutput(ProfileConfig profile)
    {
        OutputPaste = profile.Output == OutputMode.Paste;
        OutputType = profile.Output == OutputMode.Type;
        OutputClipboard = profile.Output == OutputMode.Clipboard;
        Shortcut = PasteShortcuts.FirstOrDefault(s => s.Value == profile.PasteShortcut) ?? PasteShortcuts[0];
    }

    private void LoadProvider(DictumConfig config, ProfileConfig profile, string path)
    {
        List<Option<string>> providers = [.. config.Stt.Select(kv => new Option<string>(kv.Key, ProviderLabels.For(kv.Value)))];
        if (!config.Stt.ContainsKey(profile.Stt))
        {
            providers.Add(new Option<string>(profile.Stt, string.Format(CultureInfo.CurrentCulture, Strings.SettingsProfilesProviderMissing, profile.Stt)));
        }

        if (!providers.SequenceEqual(Providers))
        {
            Providers = providers;
        }

        Provider = Providers.First(p => p.Value == profile.Stt);
        ProviderError = _session.IssueAt(ConfigPaths.Field(path, nameof(ProfileConfig.Stt)));
    }

    /// <summary>
    /// The validator puts a duplicate hotkey's error on the later profile only; the profile being edited shows it too,
    /// since that is where the hotkey was just picked.
    /// </summary>
    private string? DuplicateHotkey(DictumConfig config, int index)
    {
        if (!Dictum.Core.Hotkeys.Hotkey.TryParse(config.Profiles[index].Hotkey, out var mine, out _))
        {
            return null;
        }

        for (var i = 0; i < config.Profiles.Count; i++)
        {
            if (i != index && Dictum.Core.Hotkeys.Hotkey.TryParse(config.Profiles[i].Hotkey, out var other, out _) && other == mine
                && _session.IssueAt(ConfigPaths.Profile(i, nameof(ProfileConfig.Hotkey))) is { } issue)
            {
                return issue;
            }
        }

        return null;
    }

    private static string Caption(ProfileConfig profile)
    {
        var languages = LanguageList.Parse(profile.Language) is { } codes ? LanguageList.Format(codes) : profile.Language ?? "";
        return string.Format(CultureInfo.CurrentCulture, Strings.SettingsProfilesCaption, languages, profile.Hotkey ?? Strings.SettingsHotkeyNone);
    }

    private static string Kind(DictumConfig config, string id) => config.Processors.GetValueOrDefault(id) switch
    {
        LlmProcessorConfig => Strings.SettingsChainLlm,
        ReplaceProcessorConfig replace => string.Format(CultureInfo.CurrentCulture, Strings.SettingsChainRules, replace.Rules.Count),
        _ => Strings.SettingsChainMissing,
    };
}
