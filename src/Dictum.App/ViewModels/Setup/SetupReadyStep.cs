using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Host.Ipc;

namespace Dictum.App.ViewModels.Setup;

/// <summary>A part of the hotkey line: a key in a keycap, or the "+" between keys.</summary>
public sealed record KeyPart(string Text, bool IsKey);

/// <summary>
/// Step 8 (spec 8): the saved profile's hotkey as keycaps and how to use it, the overlay as the user will see it, a box
/// to try dictation, and the tray-pinning row unless the icon is known to be visible (SPEC:97). A line follows the
/// daemon live: starting, loading the speech model, running, or why not; another says how the last try went (PR 5b).
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

    /// <summary>The link was pushed while the step was away: read it now (the ProfilesPage pattern).</summary>
    public override void OnShown() => OnLink(daemon.Link);

    /// <param name="config">The config just saved.</param>
    /// <param name="loginError">Why the login item could not be written, or null.</param>
    public void Load(DictumConfig config, string? loginError)
    {
        var first = config.Profiles[0];
        var parts = new List<KeyPart>();
        foreach (var key in (first.Hotkey ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (parts.Count > 0)
            {
                parts.Add(new KeyPart("+", false));
            }

            parts.Add(new KeyPart(key, true));
        }

        _profile = first;
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
        { Event: "state", State: "Recording" } => Strings.OverlayListening,
        { Event: "state", State: "Transcribing" } => Strings.OverlayTranscribing,
        { Event: "state", State: "Processing" } => Strings.OverlayCleaningUp,
        { Event: "finished", Outcome: DictationOutcome.Pasted } => Strings.SetupReadyPasted,
        { Event: "finished", Outcome: DictationOutcome.Copied } => Strings.SetupReadyCopied,
        { Event: "finished", Outcome: DictationOutcome.Empty } => Strings.SetupReadyEmpty,
        { Event: "finished", Outcome: DictationOutcome.Cancelled } => Strings.OverlayCancelled,
        { Event: "finished", Outcome: DictationOutcome.Error, Message: { } why } => Format(Strings.SetupReadyDictationFailed, why),
        { Event: "finished", Outcome: DictationOutcome.Error } => Strings.OverlayFailed,
        _ => TryResult,
    };

    /// <summary>Opens the OS page; a shell that cannot open it leaves the advice on screen (final review M2).</summary>
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
            { State: DaemonLinkState.Failed } => (Format(Strings.SetupReadyFailed, _link.Problem ?? Strings.StatusFailed), false, true, true),
            { State: DaemonLinkState.VersionMismatch } => (Strings.StatusVersionMismatch, false, true, true),
            { State: DaemonLinkState.Stopped } => (Strings.StatusStopped, false, true, false),
            _ => (Strings.SetupReadyStarting, false, false, false),
        };
    }

    private (string Text, bool Ready, bool Problem, bool Restart) Connected(DaemonStatus status)
    {
        // A daemon that has just started can answer before it registered its hotkeys: not known yet (review focus 1).
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
            return (Strings.SettingsProfilesNotRegistered, false, true, false);
        }

        // Null: a daemon that does not say (an older build) is taken as running.
        return status.WarmedUp == false
            ? (Strings.SetupReadyLoading, false, false, false)
            : (Strings.SetupReadyRunning, true, false, false);
    }

    private static string Format(string format, string value) => string.Format(CultureInfo.CurrentCulture, format, value);
}
