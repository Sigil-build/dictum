using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Host.Ipc;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// Settings → Profiles: the profile list and the editor of the selected one, whose <see cref="Languages"/>,
/// <see cref="Words"/> and <see cref="Chain"/> have editors of their own. Every edit finds the profile by id inside the
/// change, as an outside edit may reorder the list. While the recorder listens, the daemon's hotkeys are paused through
/// a request queue, unless they were paused already.
/// </summary>
public sealed partial class ProfilesPage : SettingsPage
{
    private readonly SettingsSession _session;
    private readonly IDaemonChannel _channel;
    private readonly RequestQueue _requests = new();
    private string? _id;
    private bool _pausedByUs;
    private bool _loading;

    public ProfilesPage(SettingsSession session, IDaemonChannel channel)
    {
        _session = session;
        _channel = channel;
        Modes = [new(HotkeyMode.PushToTalk, Strings.SettingsProfilesPushToTalk), new(HotkeyMode.Toggle, Strings.SettingsProfilesToggle)];
        Languages = new ProfileLanguages(session, () => Current, EditProfile);
        Words = new ProfileWords(session, EditProfile);
        Chain = new ProfileChain(session, EditProfile);
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

    public IReadOnlyList<Choice<HotkeyMode>> Modes { get; }

    public ProfileLanguages Languages { get; }

    public ProfileWords Words { get; }

    public ProfileChain Chain { get; }

    public IReadOnlyList<Choice<PasteShortcut>> PasteShortcuts { get; }

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
    public partial Choice<HotkeyMode>? Mode { get; set; }

    [ObservableProperty]
    public partial string SpeechHint { get; set; } = "";

    [ObservableProperty]
    public partial bool OutputPaste { get; set; }

    [ObservableProperty]
    public partial bool OutputType { get; set; }

    [ObservableProperty]
    public partial bool OutputClipboard { get; set; }

    [ObservableProperty]
    public partial Choice<PasteShortcut>? Shortcut { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Choice<string>> Providers { get; private set; } = [];

    [ObservableProperty]
    public partial Choice<string>? Provider { get; set; }

    [ObservableProperty]
    public partial string? ProviderError { get; private set; }

    private ProfileConfig? Current => _session.Config.FindProfile(_id);

    public override void OnHidden() => IsRecording = false;

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

    partial void OnSelectedChanged(ProfileItem? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        IsRecording = false;
        _id = value.Id;
        Languages.Reset();
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

    partial void OnModeChanged(Choice<HotkeyMode>? value)
    {
        if (value is not null)
        {
            EditProfile(p => p with { Mode = value.Value });
        }
    }

    partial void OnSpeechHintChanged(string value) => EditProfile(p => p with { SttPrompt = value.Length == 0 ? null : value });

    partial void OnOutputPasteChanged(bool value) => SetOutput(value, OutputMode.Paste);

    partial void OnOutputTypeChanged(bool value) => SetOutput(value, OutputMode.Type);

    partial void OnOutputClipboardChanged(bool value) => SetOutput(value, OutputMode.Clipboard);

    partial void OnShortcutChanged(Choice<PasteShortcut>? value)
    {
        if (value is not null)
        {
            EditProfile(p => p with { PasteShortcut = value.Value });
        }
    }

    partial void OnProviderChanged(Choice<string>? value)
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
        var id = UniqueIds.For("profile", candidate => config.FindProfile(candidate) is not null);
        IsRecording = false;
        _id = id;
        Languages.Reset();
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
        Languages.Load(profile, path);
        Words.Load(profile, path);
        SpeechHint = profile.SttPrompt ?? "";
        Chain.Load(config, profile, path);
        LoadOutput(profile);
        LoadProvider(config, profile, path);
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
        List<Choice<string>> providers = [.. config.Stt.Select(kv => new Choice<string>(kv.Key, ProviderLabels.For(kv.Value)))];
        if (!config.Stt.ContainsKey(profile.Stt))
        {
            providers.Add(new Choice<string>(profile.Stt, UiText.Format(Strings.SettingsProfilesProviderMissing, profile.Stt)));
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
        return UiText.Format(Strings.SettingsProfilesCaption, languages, profile.Hotkey ?? Strings.SettingsHotkeyNone);
    }
}
