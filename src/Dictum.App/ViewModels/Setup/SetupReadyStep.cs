using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.ViewModels.Setup;

/// <summary>A part of the hotkey line: a key in a keycap, or the "+" between keys.</summary>
public sealed record KeyPart(string Text, bool IsKey);

/// <summary>
/// Step 8: the saved profile's hotkey as keycaps and how to use it, the overlay as it will look, a box to try dictation,
/// and the tray-pinning row unless the icon is known to be visible. A line follows the daemon live: starting, loading
/// the speech model, running, or why not; another says how the last try went.
/// </summary>
public sealed partial class SetupReadyStep(TrayHint tray, IDaemonChannel daemon, Action restartDaemon) : SetupStep
{
    private ProfileConfig? _profile;
    private DaemonLink? _link;

    public override string Name => Strings.SetupStepReady;

    [ObservableProperty]
    public partial IReadOnlyList<KeyPart> Keys { get; private set; } = [];

    [ObservableProperty]
    public partial string HowTo { get; private set; } = "";

    [ObservableProperty]
    public partial OverlayPalette Palette { get; private set; }

    [ObservableProperty]
    public partial string? LoginError { get; private set; }

    [ObservableProperty]
    public partial bool ShowTrayRow { get; private set; }

    [ObservableProperty]
    public partial string TrayText { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanOpenTraySettings { get; private set; }

    /// <summary>What the daemon is doing for the saved profile; empty before the save.</summary>
    [ObservableProperty]
    public partial string DaemonText { get; private set; } = "";

    /// <summary>The daemon runs, the hotkey is registered and the model is in memory: a dictation works now.</summary>
    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    /// <summary>The daemon is not running, or cannot hear the hotkey.</summary>
    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    [ObservableProperty]
    public partial bool CanRestart { get; private set; }

    /// <summary>The last try: listening, transcribing, or how it ended; null before the first.</summary>
    [ObservableProperty]
    public partial string? TryResult { get; private set; }

    /// <summary>The link may have changed while the step was away: read it again.</summary>
    public override void OnShown() => OnLink(daemon.Link);

    /// <param name="config">The config just saved.</param>
    /// <param name="loginError">Why the login item could not be written, or null.</param>
    public void Load(DictumConfig config, string? loginError)
    {
        var first = config.Profiles[0];
        var parts = new List<KeyPart>();
        foreach (var key in (first.Hotkey ?? "").Split(Dictum.Core.Hotkeys.Hotkey.Separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (parts.Count > 0)
            {
                parts.Add(new KeyPart("+", false));
            }

            parts.Add(new KeyPart(key, true));
        }

        _profile = first;
        // The try starts at the save: a dictation made earlier in the walk is not step 8's.
        TryResult = null;
        Keys = parts;
        HowTo = first.Mode == HotkeyMode.Toggle ? Strings.SetupReadyToggle : Strings.SetupReadyHold;
        Palette = config.General.Overlay.Palette;
        LoginError = loginError;
        ShowTrayRow = tray.Desktop != TrayDesktop.Other && tray.Hidden() != false;
        TrayText = tray.Desktop == TrayDesktop.Kde ? Strings.SetupTrayKde : Strings.SetupTrayWindows;
        CanOpenTraySettings = tray.OpenSettings is not null;
        Describe();
    }

    public void OnLink(DaemonLink link)
    {
        _link = link;
        Describe();
    }

    /// <summary>A pipeline state or a finished dictation; frames never come here. Any profile's dictation counts.</summary>
    public void OnEvent(IpcEvent message) => TryResult = message switch
    {
        { Event: IpcEventKind.State, State: nameof(PipelineState.Recording) } => Strings.OverlayListening,
        { Event: IpcEventKind.State, State: nameof(PipelineState.Transcribing) } => Strings.OverlayTranscribing,
        { Event: IpcEventKind.State, State: nameof(PipelineState.Processing) } => Strings.OverlayCleaningUp,
        { Event: IpcEventKind.Finished, Outcome: DictationOutcome.Pasted } => Strings.SetupReadyPasted,
        { Event: IpcEventKind.Finished, Outcome: DictationOutcome.Copied } => Strings.SetupReadyCopied,
        { Event: IpcEventKind.Finished, Outcome: DictationOutcome.Empty } => Strings.SetupReadyEmpty,
        { Event: IpcEventKind.Finished, Outcome: DictationOutcome.Cancelled } => Strings.OverlayCancelled,
        { Event: IpcEventKind.Finished, Outcome: DictationOutcome.Error, Message: { } why } => UiText.Format(Strings.SetupReadyDictationFailed, why),
        { Event: IpcEventKind.Finished, Outcome: DictationOutcome.Error } => Strings.OverlayFailed,
        _ => TryResult,
    };

    /// <summary>Opens the OS page; a shell that cannot open it leaves the advice on screen.</summary>
    [RelayCommand]
    private void OpenTraySettings()
    {
        try
        {
            tray.OpenSettings?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CanOpenTraySettings = false;
        }
    }

    [RelayCommand]
    private void Restart() => restartDaemon();

    private void Describe()
    {
        if (_profile is null || _link is null)
        {
            return;
        }

        (DaemonText, IsReady, HasProblem, CanRestart) = _link switch
        {
            { State: DaemonLinkState.Connected, Status: { } status } => Connected(status),
            { State: DaemonLinkState.Failed } => (UiText.Format(Strings.SetupReadyFailed, _link.Problem ?? Strings.StatusFailed), false, true, true),
            { State: DaemonLinkState.VersionMismatch } => (Strings.StatusVersionMismatch, false, true, true),
            { State: DaemonLinkState.Stopped } => (Strings.StatusStopped, false, true, false),
            _ => (Strings.SetupReadyStarting, false, false, false),
        };
    }

    private (string Text, bool Ready, bool Problem, bool Restart) Connected(DaemonStatus status)
    {
        // A daemon that has just started can answer before it registered its hotkeys: not known yet.
        if (status.Profiles.FirstOrDefault(p => p.Id == _profile!.Id) is not { } registration)
        {
            return (Strings.SetupReadyStarting, false, false, false);
        }

        if (status.Paused)
        {
            return (Strings.SetupReadyPaused, false, true, false);
        }

        if (registration is { Registered: false, Hotkey: not null })
        {
            // The daemon's own reason when it gives one (no hotkey backend on this desktop, a backend error); otherwise the
            // usual guess.
            return (status.Warning is { Length: > 0 } reason ? UiText.Format(Strings.SetupReadyHotkeyFailed, reason) : Strings.SettingsProfilesNotRegistered,
                false, true, false);
        }

        // Null: a daemon that does not say (an older build) is taken as running.
        return status.WarmedUp == false
            ? (Strings.SetupReadyLoading, false, false, false)
            : (Strings.SetupReadyRunning, true, false, false);
    }
}
