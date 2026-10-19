using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Platform.Hotkeys;

namespace Dictum.Host.Daemon;

/// <summary>
/// Registers a hotkey for each profile of the current config unless the hotkeys are paused, and keeps what the status
/// says about them.
/// </summary>
/// <param name="config">The config the next registration binds.</param>
/// <param name="changed">Publishes the status after each registration.</param>
internal sealed class HotkeyRegistrar(PlatformServices platform, ILog log, Func<DictumConfig> config, Action changed) : IAsyncDisposable
{
    private const string NoBackend = "none (use 'dictum toggle')";

    private IHotkeyService? _hotkeys;
    private Action<HotkeyEvent>? _onHotkey;
    private volatile bool _paused;
    private volatile string? _warning;
    private volatile IReadOnlyList<ProfileStatus> _profiles = [];

    public string Backend => _hotkeys?.Capabilities.Backend ?? NoBackend;

    public bool Paused => _paused;

    /// <summary>Why hotkeys do not work: the platform has none, or the last registration failed.</summary>
    public string? Warning => _warning;

    public IReadOnlyList<ProfileStatus> Profiles => _profiles;

    /// <summary>Creates the platform's hotkey backend, or keeps the reason there is none as the warning.</summary>
    public void Start(Action<HotkeyEvent> onHotkey)
    {
        if (platform.Unsupported is { } reason)
        {
            _warning = reason;
            log.Warn(reason);
        }
        else if (platform.CreateHotkeys is { } createHotkeys)
        {
            _hotkeys = createHotkeys();
            _hotkeys.Triggered += onHotkey;
            _onHotkey = onHotkey;
        }
    }

    public Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        _paused = paused;
        return RegisterAsync(ct);
    }

    public async Task RegisterAsync(CancellationToken ct)
    {
        var profiles = config().Profiles;
        var bindings = new List<HotkeyBinding>();
        foreach (var profile in _paused ? [] : profiles)
        {
            if (profile.Hotkey is { } text && Hotkey.TryParse(text, out var hotkey, out _))
            {
                bindings.Add(new HotkeyBinding(profile.Id, hotkey, $"Dictum: {profile.Name ?? profile.Id}"));
            }
        }

        var registered = _hotkeys is not null && await TrySetBindingsAsync(_hotkeys, bindings, ct).ConfigureAwait(false);
        _profiles = [.. profiles.Select(p => new ProfileStatus(
            p.Id,
            p.Hotkey is { } h && Hotkey.TryParse(h, out var parsed, out _) ? parsed.ToString() : null,
            p.Mode.ToString(),
            registered && bindings.Any(b => b.Id == p.Id)))];
        foreach (var binding in bindings)
        {
            log.Info($"Hotkey {binding.Hotkey} → profile '{binding.Id}'{(registered ? "" : " (not registered)")}");
        }

        changed();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hotkeys is not null)
        {
            _hotkeys.Triggered -= _onHotkey;
            await _hotkeys.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>False when the backend refused the bindings; the warning then says why, and a notification shows it.</summary>
    private async Task<bool> TrySetBindingsAsync(IHotkeyService hotkeys, List<HotkeyBinding> bindings, CancellationToken ct)
    {
        try
        {
            await hotkeys.SetBindingsAsync(bindings, ct).ConfigureAwait(false);
            _warning = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var hint = (ex as HotkeyUnavailableException)?.Hint;
            var warning = hint is null ? ex.Message : $"{ex.Message} {hint}";
            _warning = warning;
            if (ex is not HotkeyUnavailableException)
            {
                log.Error($"Unexpected hotkey backend failure: {ex.Message}", ex);
            }

            log.Warn($"Hotkeys unavailable: {warning}");
            await platform.Notifier.NotifyAsync("Dictum: hotkeys unavailable", warning, NotificationKind.Error, ct).ConfigureAwait(false);
            return false;
        }
    }
}
