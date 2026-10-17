using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Overlay;

/// <summary>
/// Turns the daemon's events into the overlay (spec 5.3). It runs the state machine and keeps the newest audio frame.
/// When a dictation starts, it asks the desktop for the pointer's screen and the reduced-motion setting, then places
/// and shows the surface. Everything runs on the UI thread except <see cref="OnFrame"/>, which the supervisor calls
/// on its own thread.
/// </summary>
public sealed class OverlayController(
    Func<IOverlaySurface> createSurface, IOverlayDesktop desktop, Func<bool> highContrast, TimeProvider time, ILog log)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    private readonly OverlayStateMachine _machine = new();
    private readonly DateTimeOffset _start = time.GetUtcNow();
    private IOverlaySurface? _surface;
    private OverlaySettings _settings = new();
    private Dictionary<string, IReadOnlyList<string>> _languages = [];
    private IpcEvent? _frame;
    private ScreenHint _hint;
    private int _probes;

    public OverlayPhase Phase => _machine.Phase;

    /// <summary>A <c>frame</c> event, on the supervisor's thread: only the newest one is kept.</summary>
    public void OnFrame(IpcEvent frame) => Volatile.Write(ref _frame, frame);

    /// <summary>Any other event, on the UI thread.</summary>
    public void OnEvent(IpcEvent message)
    {
        var now = Now();
        switch (message.Event)
        {
            case "state":
                OnState(message.State, message.Profile, now);
                break;
            case "finished":
                Finished(message, now);
                break;
            case "stopping":
                Hide();
                break;
        }
    }

    /// <summary>A link that is not Connected hides the overlay; a daemon already recording when the app connects shows it.</summary>
    public void OnLink(DaemonLink link)
    {
        if (link.State is not DaemonLinkState.Connected)
        {
            Hide();
        }
        else if (link.Status is { State: nameof(PipelineState.Recording) } status && _machine.Phase is OverlayPhase.Hidden)
        {
            OnState(status.State, status.ActiveProfile, Now());
        }
    }

    /// <summary>The overlay settings and the profiles' languages; a visible overlay moves at once.</summary>
    public void OnConfig(DictumConfig? config)
    {
        _settings = config?.General.Overlay ?? new OverlaySettings();
        _languages = [];
        foreach (var profile in config?.Profiles ?? [])
        {
            _languages[profile.Id] = LanguageList.Parse(profile.Language) ?? [];
        }

        if (!_settings.Enabled)
        {
            Hide();
        }
        else if (_machine.Phase is not OverlayPhase.Hidden)
        {
            Place();
        }
    }

    private void OnState(string? state, string? profile, TimeSpan now)
    {
        switch (state)
        {
            case nameof(PipelineState.Recording) when _settings.Enabled:
                Volatile.Write(ref _frame, null);
                _machine.Recording(now, profile is not null && _languages.TryGetValue(profile, out var codes) ? codes : []);
                _ = ShowAsync();
                break;
            case nameof(PipelineState.Transcribing):
                _machine.Transcribing(now);
                break;
            case nameof(PipelineState.Processing):
                _machine.Processing(now);
                break;
            case nameof(PipelineState.Idle):
                _machine.Idle(now);
                break;
        }
    }

    private void Finished(IpcEvent message, TimeSpan now)
    {
        switch (message.Outcome)
        {
            case DictationOutcome.Pasted or DictationOutcome.Copied:
                _machine.Done(now, message.Language);
                break;
            case DictationOutcome.Cancelled:
                _machine.Cancelled(now);
                break;
            case DictationOutcome.Empty:
                _machine.Error(now, Strings.OverlayNothingRecognised);
                break;
            default:
                _machine.Error(now, string.IsNullOrWhiteSpace(message.Message) ? Strings.OverlayFailed : message.Message);
                break;
        }
    }

    /// <summary>Asks the desktop where the pointer is, then shows the overlay unless the dictation ended meanwhile.</summary>
    private async Task ShowAsync()
    {
        var probe = ++_probes;
        OverlayProbe answer;
        try
        {
            using var timeout = new CancellationTokenSource(ProbeTimeout);

            // No ConfigureAwait: the rest runs on the UI thread.
            answer = await desktop.ProbeAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            answer = OverlayProbe.Unknown;
        }

        if (probe != _probes || _machine.Phase is OverlayPhase.Hidden)
        {
            return;
        }

        _hint = answer.Hint;
        _machine.ReducedMotion = answer.ReducedMotion;
        Place();
    }

    private void Place()
    {
        _surface ??= CreateSurface();
        if (OverlayPlacement.Choose(_surface.ScreenAreas, _hint) is not { } screen)
        {
            log.Warn("No screen to show the overlay on");
            return;
        }

        _surface.Present(
            OverlayPlacement.Place(screen, _settings.Size, _settings.Position), OverlayTokens.SizeScale(_settings.Size), _settings.Opacity, Next);
    }

    private IOverlaySurface CreateSurface()
    {
        var surface = createSurface();
        surface.ScreensChanged += () =>
        {
            if (_machine.Phase is not OverlayPhase.Hidden)
            {
                Place();
            }
        };
        return surface;
    }

    /// <summary>The surface's frame callback.</summary>
    private OverlayScene Next()
    {
        _machine.Palette = highContrast() ? OverlayPalette.Mist : _settings.Palette;
        var frame = Volatile.Read(ref _frame);
        return _machine.Advance(Now(), frame?.Rms ?? 0, frame?.Bands ?? []);
    }

    private void Hide()
    {
        _machine.Hide();
        _surface?.Dismiss();
    }

    private TimeSpan Now() => time.GetUtcNow() - _start;
}
