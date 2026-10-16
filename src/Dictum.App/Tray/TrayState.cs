using System.Globalization;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.Core.Configuration;
using Dictum.Host.Ipc;

namespace Dictum.App.Tray;

/// <summary>
/// Turns the daemon link, its events and the config into what the tray shows. Nothing here touches Avalonia or the
/// OS: the presenters (Tasks 10 and 11) draw the <see cref="TrayView"/>.
/// </summary>
public sealed class TrayState(TimeProvider time)
{
    /// <summary>Windows keeps 127 characters of a tray tooltip.</summary>
    public const int MaxTooltip = 127;

    /// <summary>An error stays on the icon this long, or until the next successful dictation.</summary>
    public static readonly TimeSpan ErrorHold = TimeSpan.FromSeconds(30);

    private DaemonLink _link = new(DaemonLinkState.Starting);
    private string _pipeline = "Idle";
    private (string Message, DateTimeOffset At)? _error;
    private Dictionary<string, string> _languages = [];
    private double? _downloads;

    /// <summary>When the view changes without an event (a held error expires), so the presenter knows when to redraw.</summary>
    public DateTimeOffset? NextChange => _error is { } error ? error.At + ErrorHold : null;

    public void OnLink(DaemonLink link)
    {
        _link = link;
        if (link.State != DaemonLinkState.Connected)
        {
            // A daemon that comes back starts idle; a stale "Recording" would stay on the icon.
            _pipeline = "Idle";
        }
    }

    public void OnEvent(IpcEvent message)
    {
        switch (message.Event)
        {
            case "state" when message.State is { } state:
                _pipeline = state;
                break;
            case "finished" when message.Outcome == DictationOutcome.Error:
                _error = (message.Message ?? Strings.TrayErrorUnknown, time.GetUtcNow());
                break;
            case "finished" when message.Outcome is DictationOutcome.Pasted or DictationOutcome.Copied:
                _error = null;
                break;
        }
    }

    /// <summary>The daemon's status has no languages, so the menu takes them from the config the app reads itself.</summary>
    public void OnConfig(DictumConfig? config)
    {
        _languages = [];
        foreach (var profile in config?.Profiles ?? [])
        {
            _languages[profile.Id] = LanguageList.Parse(profile.Language) is { } codes ? LanguageList.Format(codes) : profile.Language ?? "auto";
        }
    }

    /// <param name="progress">0–1 while models download, null otherwise.</param>
    public void OnDownloads(double? progress) => _downloads = progress;

    public TrayView View()
    {
        var (glyph, tooltip, attention, activate) = Describe(time.GetUtcNow());
        if (_downloads is { } progress)
        {
            tooltip = string.Format(CultureInfo.CurrentCulture, Strings.TrayTooltipDownloading, tooltip, progress);
        }

        return new TrayView(glyph, Shorten(tooltip), attention, Menu(), activate);
    }

    private (TrayGlyph Glyph, string Tooltip, bool Attention, TrayCommand Activate) Describe(DateTimeOffset now) => _link.State switch
    {
        DaemonLinkState.NotConfigured => (TrayGlyph.Idle, Strings.TrayTooltipNotConfigured, false, TrayCommand.Settings),
        DaemonLinkState.Starting => (TrayGlyph.Processing, Strings.TrayTooltipStarting, false, TrayCommand.Settings),
        DaemonLinkState.Restarting => (TrayGlyph.Error, Strings.TrayTooltipRestarting, true, TrayCommand.Settings),
        DaemonLinkState.Failed => (TrayGlyph.Error, Strings.TrayTooltipDaemonDown, true, TrayCommand.RestartDaemon),
        DaemonLinkState.VersionMismatch => (TrayGlyph.Error, Strings.TrayTooltipVersionMismatch, true, TrayCommand.RestartDaemon),
        DaemonLinkState.Stopped => (TrayGlyph.Idle, Strings.TrayTooltipStopped, false, TrayCommand.Settings),
        _ => DescribeConnected(now),
    };

    private (TrayGlyph Glyph, string Tooltip, bool Attention, TrayCommand Activate) DescribeConnected(DateTimeOffset now)
    {
        if (_pipeline == "Recording")
        {
            return (TrayGlyph.Recording, Strings.TrayTooltipRecording, true, TrayCommand.Settings);
        }

        if (_pipeline is "Transcribing" or "Processing" or "Output")
        {
            return (TrayGlyph.Processing, Strings.TrayTooltipTranscribing, false, TrayCommand.Settings);
        }

        if (_error is { } error && now - error.At < ErrorHold)
        {
            return (TrayGlyph.Error, Format(Strings.TrayTooltipError, error.Message), true, TrayCommand.Settings);
        }

        if (_link.Status?.Paused == true)
        {
            return (TrayGlyph.Paused, Strings.TrayTooltipPaused, false, TrayCommand.Settings);
        }

        var tooltip = SelectedProfile(_link.Status) switch
        {
            null => "Dictum",
            { Hotkey: { } hotkey } profile => Format(Strings.TrayTooltipIdle, profile.Id, hotkey),
            var profile => Format(Strings.TrayTooltipIdleNoHotkey, profile.Id),
        };
        return (TrayGlyph.Idle, tooltip, false, TrayCommand.Settings);
    }

    private List<TrayMenuItem> Menu()
    {
        var items = new List<TrayMenuItem>();
        if (_link.State is DaemonLinkState.Failed or DaemonLinkState.VersionMismatch)
        {
            items.Add(TrayMenuItem.Action(Strings.MenuRestartDaemon, TrayCommand.RestartDaemon));
            items.Add(TrayMenuItem.Separator);
        }

        if (_link is { State: DaemonLinkState.Connected, Status: { } status })
        {
            if (status.Profiles.Count > 0)
            {
                var selected = SelectedProfile(status)?.Id;
                items.Add(TrayMenuItem.Header(Strings.MenuProfileHeader));
                foreach (var profile in status.Profiles)
                {
                    items.Add(TrayMenuItem.ForProfile(profile.Id, profile.Id == selected, _languages.GetValueOrDefault(profile.Id)));
                }

                items.Add(TrayMenuItem.Separator);
            }

            items.Add(status.Paused
                ? TrayMenuItem.Action(Strings.MenuResumeHotkeys, TrayCommand.ResumeHotkeys)
                : TrayMenuItem.Action(Strings.MenuPauseHotkeys, TrayCommand.PauseHotkeys));
        }

        items.Add(TrayMenuItem.Action(Strings.MenuHistory, TrayCommand.History));
        items.Add(TrayMenuItem.Action(Strings.MenuSettings, TrayCommand.Settings));
        items.Add(TrayMenuItem.Separator);
        items.Add(TrayMenuItem.Action(Strings.MenuQuit, TrayCommand.Quit));
        return items;
    }

    private static ProfileStatus? SelectedProfile(DaemonStatus? status) =>
        status?.Profiles.FirstOrDefault(p => p.Id == status.SelectedProfile) ?? status?.Profiles.FirstOrDefault();

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Shorten(string text) =>
        text.Length <= MaxTooltip ? text : string.Concat(text.AsSpan(0, MaxTooltip - 1), "…");
}
