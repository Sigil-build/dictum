using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Audio.PortAudio;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the General page uses besides the config; fakes in tests.</summary>
/// <param name="Autostart">Null where Dictum has no login item (macOS).</param>
/// <param name="Executable">dictum-gui's path for the login item; null when unknown.</param>
public sealed record GeneralServices(
    IDaemonChannel Channel,
    FrameHub Frames,
    ITimers Timers,
    IAutostart? Autostart,
    string? Executable,
    ISoundPlayer Sounds,
    TimeProvider Time,
    ILog Log);

/// <summary>
/// Settings → General:
/// <list type="bullet">
/// <item>Start at login writes the OS login item for dictum-gui; it is not a config setting.</item>
/// <item>The microphone list comes from the daemon's <c>devices</c>.</item>
/// <item>The meter runs only while the page is shown. <c>monitor-start</c> is renewed every 10 s and
/// <c>monitor-stop</c> is sent when the page hides. A running monitor keeps its device, so a new microphone means
/// stop, then start.</item>
/// <item>The other rows edit the config through the session.</item>
/// </list>
/// </summary>
public sealed partial class GeneralPage : SettingsPage
{
    public static readonly TimeSpan MeterTick = TimeSpan.FromMilliseconds(50);

    private static readonly string HistorySizePath = ConfigPaths.General(nameof(GeneralSettings.HistorySize));

    /// <summary>Well inside <see cref="IpcProtocol.MonitorLifetime"/>, so the daemon keeps the meter open while the page shows.</summary>
    private static readonly TimeSpan MonitorRenewal = TimeSpan.FromSeconds(10);

    private readonly SettingsSession _session;
    private readonly GeneralServices _services;
    private readonly InputMeter _meter = new();
    private readonly DateTimeOffset _epoch;
    private readonly RequestQueue _monitor = new();
    private IReadOnlyList<AudioDevice> _devices = [];
    private bool _devicesKnown;
    private IDisposable? _tick;
    private IDisposable? _renew;
    private bool _shown;
    private bool _monitoring;
    private bool _loading;

    /// <summary>The History field is empty: the session holds the file's value and the field keeps its error.</summary>
    private bool _historyEmpty;

    public GeneralPage(SettingsSession session, GeneralServices services)
    {
        _session = session;
        _services = services;
        _epoch = services.Time.GetUtcNow();
        UiLanguages =
        [
            new Option<string?>(null, Strings.SettingsUiLanguageSystem),
            new Option<string?>("en", Strings.SettingsUiLanguageEnglish),
            new Option<string?>("uk", Strings.SettingsUiLanguageUkrainian),
        ];
        CanChangeStartAtLogin = services.Autostart is not null && services.Executable is not null;
        MeterText = InputMeter.Readout((int)InputMeter.FloorDb);
        session.Changed += Load;
        Load();
        ReadLoginItem();
        Overlay = new OverlayPage(session, Strings.SettingsGeneral);
        Pasting = new PastingPage(session, Strings.SettingsGeneral, OperatingSystem.IsLinux());
    }

    public override string Title => Strings.SettingsGeneral;

    public IReadOnlyList<Option<string?>> UiLanguages { get; }

    public OverlayPage Overlay { get; }

    public PastingPage Pasting { get; }

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    public bool CanChangeStartAtLogin { get; }

    [ObservableProperty]
    public partial string? StartAtLoginError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<string?>> Microphones { get; private set; } = [];

    [ObservableProperty]
    public partial Option<string?>? Microphone { get; set; }

    [ObservableProperty]
    public partial double MeterLevel { get; private set; }

    [ObservableProperty]
    public partial string MeterText { get; private set; }

    [ObservableProperty]
    public partial string? MeterNote { get; private set; }

    [ObservableProperty]
    public partial bool Sounds { get; set; }

    [ObservableProperty]
    public partial decimal? HistorySize { get; set; }

    [ObservableProperty]
    public partial string? HistoryError { get; private set; }

    [ObservableProperty]
    public partial Option<string?>? UiLanguage { get; set; }

    /// <summary>General → "Run setup again": the shell opens the wizard with the current values.</summary>
    public event Action? SetupRequested;

    public override void OnShown()
    {
        _shown = true;
        ReadLoginItem();
        _tick = _services.Timers.Every(MeterTick, Tick);
        _renew = _services.Timers.Every(MonitorRenewal, () => Queue(StartMonitorAsync));
        _ = RefreshAsync();
    }

    public override void OnHidden()
    {
        _shown = false;
        _tick?.Dispose();
        _renew?.Dispose();
        _tick = null;
        _renew = null;
        _meter.Reset();
        MeterLevel = 0;
        Queue(StopMonitorAsync);
    }

    public override void OnLink(DaemonLink link)
    {
        if (link.State != DaemonLinkState.Connected)
        {
            // The daemon's monitor went with it.
            _monitoring = false;
            if (_shown)
            {
                MeterNote = Strings.SettingsMeterNoDaemon;
            }

            return;
        }

        if (_shown)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (!_loading && CanChangeStartAtLogin)
        {
            _ = ApplyLoginItemAsync(value);
        }
    }

    partial void OnMicrophoneChanged(Option<string?>? value)
    {
        if (_loading || value is null || value.Value == _session.Config.General.AudioDevice)
        {
            return;
        }

        _session.Edit(c => c with { General = c.General with { AudioDevice = value.Value } });
        if (_shown)
        {
            Queue(StopMonitorAsync);
            Queue(StartMonitorAsync);
        }
    }

    partial void OnSoundsChanged(bool value)
    {
        if (!_loading)
        {
            _session.Edit(c => c with { General = c.General with { Sounds = value } });
        }
    }

    partial void OnHistorySizeChanged(decimal? value)
    {
        if (_loading)
        {
            return;
        }

        if (value is not { } size)
        {
            // Backspacing "100" passes through 10 and 1: the copy goes back to the file's value, so no number typed
            // on the way is saved, and other rows still save.
            _historyEmpty = true;
            var saved = _session.OnDisk.General.HistorySize;
            _session.Edit(c => c with { General = c.General with { HistorySize = saved } });
            HistoryError = Strings.SettingsNumberRequired;
            return;
        }

        _historyEmpty = false;
        _session.Edit(c => c with { General = c.General with { HistorySize = (int)decimal.Truncate(size) } });
        HistoryError = _session.IssueAt(HistorySizePath);
    }

    partial void OnUiLanguageChanged(Option<string?>? value)
    {
        if (!_loading && value is not null)
        {
            _session.Edit(c => c with { General = c.General with { UiLanguage = value.Value } });
        }
    }

    [RelayCommand]
    private void OpenOverlay() => Navigate(Overlay);

    [RelayCommand]
    private void OpenPasting() => Navigate(Pasting);

    [RelayCommand]
    private void RunSetup() => SetupRequested?.Invoke();

    [RelayCommand]
    private async Task PreviewAsync()
    {
        try
        {
            await _services.Sounds.PlayAsync(SoundCue.Start, CancellationToken.None);
            await _services.Sounds.PlayAsync(SoundCue.Stop, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A command that throws would take dictum-gui down; a silent preview is the whole cost.
            _services.Log.Warn($"Sound preview failed: {ex.Message}");
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var general = _session.Config.General;
            CanEdit = _session.CanEdit;
            Sounds = general.Sounds;
            if (!_historyEmpty)
            {
                HistorySize = general.HistorySize;
                HistoryError = _session.IssueAt(HistorySizePath);
            }
            UiLanguage = UiLanguages.FirstOrDefault(l => l.Value == general.UiLanguage) ?? UiLanguages[0];
            var microphones = MicrophoneList(_devices, _devicesKnown, general.AudioDevice);
            if (!microphones.SequenceEqual(Microphones))
            {
                Microphones = microphones;
            }

            Microphone = Microphones.FirstOrDefault(m => m.Value == general.AudioDevice) ?? Microphones[0];
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The system default first (named after the default device when the daemon reported one), then each device.</summary>
    /// <param name="known">The daemon reported its devices; only then can a configured one be called missing.</param>
    private static List<Option<string?>> MicrophoneList(IReadOnlyList<AudioDevice> devices, bool known, string? configured)
    {
        var standard = devices.FirstOrDefault(d => d.IsDefault) is { } device
            ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsMicrophoneDefault, device.Name)
            : Strings.SettingsMicrophoneSystemDefault;
        var list = new List<Option<string?>> { new(null, standard) };
        list.AddRange(devices.Select(d => d.Name).Distinct().Select(name => new Option<string?>(name, name)));
        if (configured is not null && !devices.Any(d => d.Name == configured))
        {
            list.Add(new Option<string?>(configured,
                known ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsMicrophoneMissing, configured) : configured));
        }

        return list;
    }

    private void ReadLoginItem()
    {
        _loading = true;
        StartAtLogin = _services.Autostart?.Installed == LoginApp.DesktopApp;
        _loading = false;
    }

    private async Task ApplyLoginItemAsync(bool enable)
    {
        try
        {
            await _services.Autostart!.ApplyAsync(enable, new LoginItem(LoginApp.DesktopApp, _services.Executable!), CancellationToken.None);
            StartAtLoginError = null;
        }
        catch (Exception ex)
        {
            // Each OS stores the login item its own way (registry, autostart file, portal); any failure shows by the switch.
            _services.Log.Warn($"Login item not changed: {ex.Message}");
            StartAtLoginError = string.Format(CultureInfo.CurrentCulture, Strings.SettingsStartAtLoginFailed, ex.Message);
            ReadLoginItem();
        }
    }

    private async Task RefreshAsync()
    {
        if (await _services.Channel.TrySendAsync(new IpcRequest(IpcCommand.Devices), CancellationToken.None) is { Ok: true, Devices: { } devices })
        {
            _devices = devices;
            _devicesKnown = true;
            Load();
        }

        Queue(StartMonitorAsync);
    }

    /// <summary>
    /// Monitor requests go one after another: a stop sent before a start could otherwise reach the daemon after it, and
    /// a switch during a start in flight would send no stop at all.
    /// </summary>
    private void Queue(Func<Task> step) => _monitor.Enqueue(step);

    private async Task StartMonitorAsync()
    {
        if (!_shown)
        {
            return;
        }

        var request = new IpcRequest(IpcCommand.MonitorStart, Device: _session.Config.General.AudioDevice);
        var answer = await _services.Channel.TrySendAsync(request, CancellationToken.None);

        // Hidden while the request was out: the stop queued behind it closes what it opened.
        _monitoring = answer?.Ok == true;
        if (_shown)
        {
            MeterNote = answer is null ? Strings.SettingsMeterNoDaemon : answer.Ok ? null : answer.Error;
        }
    }

    private async Task StopMonitorAsync()
    {
        if (!_monitoring)
        {
            return;
        }

        _monitoring = false;
        await _services.Channel.TrySendAsync(new IpcRequest(IpcCommand.MonitorStop), CancellationToken.None);
    }

    private void Tick()
    {
        _meter.Update(_services.Frames.Rms(InputMeter.Stale), _services.Time.GetUtcNow() - _epoch);
        MeterLevel = _meter.Level;
        MeterText = InputMeter.Readout(_meter.Db);
    }
}
