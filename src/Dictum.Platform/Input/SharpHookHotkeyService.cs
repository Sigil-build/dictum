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
public sealed class SharpHookHotkeyService(ILog log, string backend) : IHotkeyService, IModifierState
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly bool _suppress = OperatingSystem.IsWindows();
    private SimpleGlobalHook? _hook;
    private Task? _run;
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

        if (_hook is null && bindings.Count > 0)
        {
            await StartAsync(ct).ConfigureAwait(false);
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

            await Task.Delay(15, ct).ConfigureAwait(false);
        }

        log.Debug("Modifiers still held after waiting; pasting anyway");
    }

    public async ValueTask DisposeAsync()
    {
        if (_hook is { } hook)
        {
            hook.Dispose();
            if (_run is not null)
            {
                try
                {
                    await _run.ConfigureAwait(false);
                }
                catch (HookException)
                {
                }
            }
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new SimpleGlobalHook();
        hook.HookEnabled += (_, _) => enabled.TrySetResult();
        hook.KeyPressed += OnKeyPressed;
        hook.KeyReleased += OnKeyReleased;
        _hook = hook;
        _run = hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);

        var finished = await Task.WhenAny(enabled.Task, _run, Task.Delay(StartTimeout, ct)).ConfigureAwait(false);
        if (finished == _run && _run.Exception?.InnerException is HookException hookError)
        {
            throw new HotkeyUnavailableException($"The keyboard hook failed to start: {hookError.Result}", HintFor(hookError.Result), hookError);
        }

        if (finished != enabled.Task)
        {
            throw new HotkeyUnavailableException("The keyboard hook did not start in time.");
        }

        log.Info($"Keyboard hook running ({backend})");
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
}
