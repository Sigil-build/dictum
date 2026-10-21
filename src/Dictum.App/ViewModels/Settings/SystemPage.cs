using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Shell;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Host.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the System page uses; fakes in tests.</summary>
/// <param name="Service">The daemon card: the status window's view model.</param>
/// <param name="Overlay">The overlay row, read on every check.</param>
/// <param name="Tray">The tray icon row, read on every check.</param>
/// <param name="Desktop">"Windows 11", "Wayland, KDE": the header's first part.</param>
/// <param name="OpenDocs">A problem row's "How to fix": the troubleshooting section for its check.</param>
public sealed record SystemServices(
    IDaemonChannel Channel,
    StatusViewModel Service,
    Func<SystemRow> Overlay,
    Func<SystemRow> Tray,
    ITimers Timers,
    TimeProvider Time,
    string Desktop,
    OpenDocs OpenDocs);

/// <summary>
/// Settings → System. It shows the daemon's <c>doctor</c> checks, then the app's own overlay and tray rows,
/// with a summary and the daemon card. The checks run each time the page shows, on "Check again", and when the
/// daemon comes back after a check that could not reach it. A check that is not OK, and the row that says the daemon
/// could not run them, link to the troubleshooting section that <c>dictum doctor</c>'s <c>see:</c> line names.
/// </summary>
public sealed partial class SystemPage : SettingsPage
{
    /// <summary>Each provider check may take 15 s.</summary>
    public static readonly TimeSpan DoctorTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The header's "checked N s ago" counts seconds.</summary>
    private static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(1);

    private readonly SystemServices _services;
    private IDisposable? _clock;
    private DateTimeOffset? _checkedAt;
    private bool _shown;
    private bool _daemonMissing;
    private int _run;

    public SystemPage(SystemServices services)
    {
        _services = services;
        Header = services.Desktop;
    }

    public override string Title => Strings.SettingsSystem;

    public override string DocsAnchor => DocsAnchors.Settings.System;

    public StatusViewModel Service => _services.Service;

    [ObservableProperty]
    public partial IReadOnlyList<SystemRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial bool HasProblems { get; private set; }

    [ObservableProperty]
    public partial string Header { get; private set; }

    [ObservableProperty]
    public partial bool Checking { get; private set; }

    /// <summary>The daemon's check names (DoctorChecks and each platform), in the UI language; new ones keep their name.</summary>
    public static string CheckName(string name) => name switch
    {
        PlatformCheckNames.Config => Strings.SettingsCheckConfig,
        PlatformCheckNames.ConfigWarnings => Strings.SettingsCheckConfigWarnings,
        PlatformCheckNames.Models => Strings.SettingsCheckModels,
        PlatformCheckNames.Gpu => Strings.SettingsCheckGpu,
        PlatformCheckNames.AudioLibrary => Strings.SettingsCheckAudioLibrary,
        PlatformCheckNames.Microphone => Strings.SettingsMicrophone,
        PlatformCheckNames.Platform => Strings.SettingsCheckPlatform,
        PlatformCheckNames.Session => Strings.SettingsCheckSession,
        PlatformCheckNames.Hotkeys => Strings.SettingsCheckHotkeys,
        PlatformCheckNames.Paste => Strings.SettingsCheckPaste,
        PlatformCheckNames.PastePortal => Strings.SettingsCheckPastePortal,
        PlatformCheckNames.PasteYdotool => Strings.SettingsCheckPasteYdotool,
        PlatformCheckNames.PasteOrder => Strings.SettingsCheckPasteOrder,
        PlatformCheckNames.Clipboard => Strings.SettingsCheckClipboard,
        PlatformCheckNames.ActiveWindow => Strings.SettingsCheckActiveWindow,
        PlatformCheckNames.Secrets => Strings.SettingsCheckSecrets,
        PlatformCheckNames.Elevation => Strings.SettingsCheckElevation,
        PlatformCheckNames.Daemon => Strings.SettingsCheckDaemon,
        _ when name.StartsWith(PlatformCheckNames.SttPrefix, StringComparison.Ordinal) =>
            UiText.Format(Strings.SettingsCheckStt, name[PlatformCheckNames.SttPrefix.Length..]),
        _ when name.StartsWith(PlatformCheckNames.ProcessorPrefix, StringComparison.Ordinal) =>
            UiText.Format(Strings.SettingsCheckProcessor, name[PlatformCheckNames.ProcessorPrefix.Length..]),
        _ => name,
    };

    public override void OnShown()
    {
        _shown = true;
        _clock = _services.Timers.Every(ClockTick, UpdateHeader);
        _ = CheckAsync();
    }

    public override void OnHidden()
    {
        _shown = false;
        _clock?.Dispose();
        _clock = null;
    }

    public override void OnLink(DaemonLink link)
    {
        if (_shown && _daemonMissing && link.State == DaemonLinkState.Connected)
        {
            _ = CheckAsync();
        }
    }

    [RelayCommand]
    private Task CheckAgainAsync() => CheckAsync();

    private async Task CheckAsync()
    {
        var run = ++_run;
        Checking = true;
        UpdateHeader();
        var answer = await _services.Channel.TrySendAsync(new IpcRequest(IpcCommand.Doctor), CancellationToken.None, DoctorTimeout);
        if (run != _run)
        {
            return;
        }

        _daemonMissing = answer is null;
        var rows = new List<SystemRow>();
        if (answer is { Ok: true, Checks: { } checks })
        {
            rows.AddRange(checks.Select(Row));
        }
        else
        {
            rows.Add(new SystemRow(Strings.SettingsCheckDaemon, HealthLevel.Error,
                answer?.Error ?? Strings.SettingsSystemNoDaemon, answer is null ? Strings.SettingsSystemNoDaemonDetail : null,
                Help: HowToFix(DocsAnchors.Troubleshooting.Daemon)));
        }

        rows.Add(_services.Overlay());
        rows.Add(_services.Tray());
        Rows = rows;

        var problems = rows.Count(r => r.Level == HealthLevel.Error);
        var warnings = rows.Count(r => r.Level == HealthLevel.Warning);
        HasProblems = problems + warnings > 0;
        Summary = HasProblems
            ? UiText.Format(Strings.SettingsSystemSummary, problems, warnings)
            : Strings.SettingsSystemAllGood;
        _checkedAt = _services.Time.GetUtcNow();
        Checking = false;
        UpdateHeader();
    }

    /// <summary>A doctor check as a row; one that is not OK links to its troubleshooting section, as doctor's <c>see:</c> line does.</summary>
    private SystemRow Row(PlatformCheck check) => new(CheckName(check.Name), check.Status.Level, check.Status.Detail, check.Status.Hint,
        Help: check.Status.Level == HealthLevel.Ok ? null : HowToFix(DoctorDocs.AnchorFor(check.Name)));

    private RelayCommand HowToFix(string anchor) => new(() => _services.OpenDocs(DocsPage.Troubleshooting, anchor));

    private void UpdateHeader() => Header = Checking || _checkedAt is not { } at
        ? UiText.Format(Strings.SettingsSystemChecking, _services.Desktop)
        : UiText.Format(Strings.SettingsSystemChecked, _services.Desktop, (int)(_services.Time.GetUtcNow() - at).TotalSeconds);
}
