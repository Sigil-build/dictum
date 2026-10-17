using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the System page uses; fakes in tests.</summary>
/// <param name="Service">The daemon card: the status window's model, now part of this page.</param>
/// <param name="Overlay">The overlay row, read on every check.</param>
/// <param name="Tray">The tray icon row, read on every check.</param>
/// <param name="Desktop">"Windows 11", "Wayland, KDE": the header's first part.</param>
public sealed record SystemServices(
    IDaemonChannel Channel,
    StatusViewModel Service,
    Func<SystemRow> Overlay,
    Func<SystemRow> Tray,
    ITimers Timers,
    TimeProvider Time,
    string Desktop);

/// <summary>
/// Settings → System (spec 7). It shows the daemon's <c>doctor</c> checks, then the app's own overlay and tray rows,
/// with a summary and the daemon card. The checks run each time the page shows, on "Check again", and when the
/// daemon comes back after a check that could not reach it.
/// </summary>
public sealed partial class SystemPage : SettingsPage
{
    /// <summary>Each provider check may take 15 s (code map §3).</summary>
    public static readonly TimeSpan DoctorTimeout = TimeSpan.FromMinutes(2);

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

    public static SystemRow Row(PlatformCheck check) => new(CheckName(check.Name), check.Status.Level, check.Status.Detail, check.Status.Hint);

    /// <summary>The daemon's check names (DoctorChecks and each platform), in the UI language; new ones keep their name.</summary>
    public static string CheckName(string name) => name switch
    {
        "config" => Strings.SettingsCheckConfig,
        "config warnings" => Strings.SettingsCheckConfigWarnings,
        "models" => Strings.SettingsCheckModels,
        "gpu" => Strings.SettingsCheckGpu,
        "audio library" => Strings.SettingsCheckAudioLibrary,
        "microphone" => Strings.SettingsMicrophone,
        "platform" => Strings.SettingsCheckPlatform,
        "session" => Strings.SettingsCheckSession,
        "hotkeys" => Strings.SettingsCheckHotkeys,
        "paste" => Strings.SettingsCheckPaste,
        "paste (portal)" => Strings.SettingsCheckPastePortal,
        "paste (ydotool)" => Strings.SettingsCheckPasteYdotool,
        "paste order" => Strings.SettingsCheckPasteOrder,
        "clipboard" => Strings.SettingsCheckClipboard,
        "active window" => Strings.SettingsCheckActiveWindow,
        "secrets" => Strings.SettingsCheckSecrets,
        "elevation" => Strings.SettingsCheckElevation,
        "daemon" => Strings.SettingsCheckDaemon,
        _ when name.StartsWith("stt:", StringComparison.Ordinal) =>
            string.Format(CultureInfo.CurrentCulture, Strings.SettingsCheckStt, name["stt:".Length..]),
        _ when name.StartsWith("processor:", StringComparison.Ordinal) =>
            string.Format(CultureInfo.CurrentCulture, Strings.SettingsCheckProcessor, name["processor:".Length..]),
        _ => name,
    };

    public override void OnShown()
    {
        _shown = true;
        _clock = _services.Timers.Every(TimeSpan.FromSeconds(1), UpdateHeader);
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
        var answer = await _services.Channel.TrySendAsync(new IpcRequest("doctor"), CancellationToken.None, DoctorTimeout);
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
                answer?.Error ?? Strings.SettingsSystemNoDaemon, answer is null ? Strings.SettingsSystemNoDaemonDetail : null));
        }

        rows.Add(_services.Overlay());
        rows.Add(_services.Tray());
        Rows = rows;

        var problems = rows.Count(r => r.Level == HealthLevel.Error);
        var warnings = rows.Count(r => r.Level == HealthLevel.Warning);
        HasProblems = problems + warnings > 0;
        Summary = HasProblems
            ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsSystemSummary, problems, warnings)
            : Strings.SettingsSystemAllGood;
        _checkedAt = _services.Time.GetUtcNow();
        Checking = false;
        UpdateHeader();
    }

    private void UpdateHeader() => Header = Checking || _checkedAt is not { } at
        ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsSystemChecking, _services.Desktop)
        : string.Format(CultureInfo.CurrentCulture, Strings.SettingsSystemChecked, _services.Desktop, (int)(_services.Time.GetUtcNow() - at).TotalSeconds);
}
