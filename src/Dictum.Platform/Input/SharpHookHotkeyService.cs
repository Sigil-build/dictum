using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Injection;
using SharpHook;
using SharpHook.Data;

namespace Dictum.Platform.Input;

/// <summary>
/// Global hotkeys through a low-level keyboard hook (Windows, Linux X11). Pressing the combination
/// raises Pressed; releasing any key of it raises Released. On Windows the combination is suppressed
/// so it does not also reach the focused app.
/// </summary>
/// <param name="createHook">Makes the hook for each start; null uses SharpHook's libuiohook hook.</param>
/// <param name="startTimeout">How long a starting hook may take to enable.</param>
public sealed class SharpHookHotkeyService(ILog log, string backend, Func<IGlobalHook>? createHook = null, TimeSpan? startTimeout = null)
    : IHotkeyService, IModifierState
{
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(15);

    private readonly Lock _gate = new();
    private readonly bool _suppress = OperatingSystem.IsWindows();
    private readonly Func<IGlobalHook> _createHook = createHook ?? (static () => new SimpleGlobalHook());
    private readonly TimeSpan _startTimeout = startTimeout ?? DefaultStartTimeout;
    private StartedHook? _hook;
    private IReadOnlyList<HotkeyBinding> _bindings = [];
    private HotkeyModifiers _held;
    private HotkeyBinding? _active;
    private KeyCode _activeKey;

    public event Action<HotkeyEvent>? Triggered;

    public HotkeyCapabilities Capabilities => new(SupportsRelease: true, NeedsUserConfirmation: false, CanSuppress: _suppress, Backend: backend);

    public async Task SetBindingsAsync(IReadOnlyList<HotkeyBinding> bindings, CancellationToken ct)
    {
        lock (_gate)
        {
            _bindings = bindings;
            _active = null;
        }

        if (bindings.Count == 0)
        {
            return;
        }

        var hook = _hook;
        if (hook is { Run.IsCompleted: true, Enabled.IsCompleted: false })
        {
            // Its run ended without enabling after its start timed out; with that run over, a new hook does not run beside it.
            Abandon(hook);
            hook = null;
        }

        hook ??= Start();
        if (!hook.Enabled.IsCompleted)
        {
            await WaitUntilEnabledAsync(hook, ct).ConfigureAwait(false);
        }
    }

    public async Task WaitForReleaseAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_held == HotkeyModifiers.None)
                {
                    return;
                }
            }

            await Task.Delay(ReleasePollInterval, ct).ConfigureAwait(false);
        }

        log.Debug("Modifiers still held after waiting; pasting anyway");
    }

    public async ValueTask DisposeAsync()
    {
        if (_hook is { } started)
        {
            started.Hook.Dispose();
            try
            {
                await started.Run.ConfigureAwait(false);
            }
            catch (HookException)
            {
                // A hook that stopped with an error has nothing left to release, and disposing must not throw.
            }
        }
    }

    private StartedHook Start()
    {
        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = _createHook();
        hook.HookEnabled += (_, _) => enabled.TrySetResult();
        hook.KeyPressed += OnKeyPressed;
        hook.KeyReleased += OnKeyReleased;
        var started = new StartedHook(hook, hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true), enabled.Task);
        _hook = started;
        return started;
    }

    /// <summary>
    /// A hook still starting when the time is up is kept: it delivers the hotkeys if it enables later, and the next
    /// bindings wait on it again, since a second native run beside it would leave two hooks installed.
    /// </summary>
    private async Task WaitUntilEnabledAsync(StartedHook started, CancellationToken ct)
    {
        var finished = await Task.WhenAny(started.Enabled, started.Run, Task.Delay(_startTimeout, ct)).ConfigureAwait(false);
        if (finished == started.Enabled)
        {
            log.Info($"Keyboard hook running ({backend})");
            return;
        }

        if (finished == started.Run)
        {
            Abandon(started);
            if (started.Run.Exception?.InnerException is HookException hookError)
            {
                throw new HotkeyUnavailableException($"The keyboard hook failed to start: {hookError.Result}", HintFor(hookError.Result), hookError);
            }
        }
        else if (ct.IsCancellationRequested)
        {
            // Cancelled means shutting down: nothing will want the hook, even if it starts later.
            Abandon(started);
        }

        ct.ThrowIfCancellationRequested();
        throw new HotkeyUnavailableException("The keyboard hook did not start in time.");
    }

    /// <summary>Drops a hook whose run ended before it enabled, or whose start was cancelled, so the next bindings start a new one.</summary>
    private void Abandon(StartedHook started)
    {
        _hook = null;
        started.Hook.KeyPressed -= OnKeyPressed;
        started.Hook.KeyReleased -= OnKeyReleased;
        try
        {
            started.Hook.Dispose();
        }
        catch (HookException)
        {
            // Stopping a hook that never enabled can fail; without its handlers it raises nothing even if it starts later.
        }
    }

    private static string? HintFor(UioHookResult result) => result switch
    {
        // libuiohook-xrecord.so (SharpHook 8) links libX11, libXtst, libXt, libXrandr, libxkbcommon and needs glibc 2.38+.
        UioHookResult.ErrorLinuxLoadBackend =>
            "The X11 hook needs glibc 2.38+ (e.g. Ubuntu 24.04+) and the libraries libXtst, libXt, libXrandr, libxkbcommon "
            + "('sudo apt install libxtst6 libxt6 libxrandr2 libxkbcommon0'). Otherwise bind 'dictum toggle' in your desktop's shortcut settings.",
        UioHookResult.ErrorAxApiDisabled => "Allow Dictum in System Settings → Privacy & Security → Accessibility.",
        _ => null,
    };

    // Runs on the hook thread: keep it short and never block.
    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated)
        {
            return;
        }

        var code = e.Data.KeyCode;
        HotkeyEvent? raise = null;
        lock (_gate)
        {
            var modifier = SharpHookKeys.ModifierOf(code);
            if (modifier != HotkeyModifiers.None)
            {
                _held |= modifier;
                return;
            }

            if (_active is not null)
            {
                // Auto-repeat of the held key.
                e.SuppressEvent = _suppress && code == _activeKey;
                return;
            }

            if (SharpHookKeys.FromKeyCode(code) is { } key
                && _bindings.FirstOrDefault(b => b.Hotkey.Key == key && b.Hotkey.Modifiers == _held) is { } binding)
            {
                _active = binding;
                _activeKey = code;
                e.SuppressEvent = _suppress;
                raise = new HotkeyEvent(binding.Id, HotkeyPhase.Pressed);
            }
        }

        Raise(raise);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated)
        {
            return;
        }

        var code = e.Data.KeyCode;
        HotkeyEvent? raise = null;
        lock (_gate)
        {
            var modifier = SharpHookKeys.ModifierOf(code);
            _held &= ~modifier;

            if (_active is { } active && (code == _activeKey || (modifier != HotkeyModifiers.None && active.Hotkey.Modifiers.HasFlag(modifier))))
            {
                e.SuppressEvent = _suppress && code == _activeKey;
                raise = new HotkeyEvent(active.Id, HotkeyPhase.Released);
                _active = null;
            }
        }

        Raise(raise);
    }

    private void Raise(HotkeyEvent? hotkeyEvent)
    {
        if (hotkeyEvent is not null && Triggered is { } handler)
        {
            ThreadPool.QueueUserWorkItem(static state => state.handler(state.hotkeyEvent), (handler, hotkeyEvent), preferLocal: false);
        }
    }

    /// <summary>A hook and its run, held in one field so that a reader never sees one without the other.</summary>
    /// <param name="Enabled">Completes once the hook is installed and delivers keys.</param>
    private sealed record StartedHook(IGlobalHook Hook, Task Run, Task Enabled);
}
