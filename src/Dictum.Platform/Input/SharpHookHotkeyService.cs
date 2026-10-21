using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Injection;
using SharpHook;
using SharpHook.Data;
using SharpHook.Providers;

namespace Dictum.Platform.Input;

/// <summary>
/// Global hotkeys through a low-level keyboard hook (Windows, Linux X11). Pressing the combination
/// raises Pressed; releasing any key of it raises Released. On Windows the combination is suppressed
/// so it does not also reach the focused app. The hook runs only while there are bindings: an empty set, as
/// Pause all hotkeys sends, removes it.
/// </summary>
/// <remarks>
/// At most one native run exists at a time: a new hook starts only once the run of the one before it has ended, as
/// <see cref="RunTracker"/> sees it.
/// </remarks>
/// <param name="provider">Gives the native hook for each start; null uses libuiohook.</param>
/// <param name="startTimeout">How long a starting hook may take to enable.</param>
public sealed class SharpHookHotkeyService(ILog log, string backend, Func<IGlobalHookProvider>? provider = null, TimeSpan? startTimeout = null)
    : IHotkeyService, IModifierState
{
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a hook's run may take to end once it is asked to stop; a native stop returns within milliseconds.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(15);

    private readonly Lock _gate = new();
    private readonly bool _suppress = OperatingSystem.IsWindows();
    private readonly Func<IGlobalHookProvider> _provider = provider ?? (static () => UioHookProvider.Instance);
    private readonly TimeSpan _startTimeout = startTimeout ?? DefaultStartTimeout;
    private StartedHook? _hook;

    /// <summary>The last hook let go of while its run had not ended yet; no new hook starts until it has.</summary>
    private StartedHook? _ending;

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
            // Paused, or no profile has a hotkey: let go of the keyboard, so nothing sees the keys while Dictum does not listen.
            await StopAsync().ConfigureAwait(false);
            return;
        }

        var hook = _hook;
        if (hook is { Run.IsCompleted: true })
        {
            // A hook whose run is over hears nothing: it failed after its start timed out, or a stop reached it late.
            Abandon(hook);
            hook = null;
        }

        hook ??= await StartAsync(ct).ConfigureAwait(false);
        if (!hook.Enabled.IsCompleted)
        {
            await WaitUntilEnabledAsync(hook, mayRetry: true, ct).ConfigureAwait(false);
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
            _hook = null;
            await LetGoAsync(started).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a new hook once the run of the one before it has ended. Until its thread has cleared libuiohook's
    /// dispatch procedure, which every hook in the process shares, a new hook would lose its procedure to that clear
    /// and never hear a key.
    /// </summary>
    private async Task<StartedHook> StartAsync(CancellationToken ct)
    {
        if (_ending is { } ending)
        {
            if (!await EndsWithinAsync(ending, StopTimeout, ct).ConfigureAwait(false))
            {
                // Two native runs at once would leave two hooks installed, and the old one's end would break the new one.
                throw new HotkeyUnavailableException("The previous keyboard hook has not stopped yet.");
            }

            _ending = null;
        }

        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new RunTracker(_provider());
        var hook = new SimpleGlobalHook(native);
        hook.HookEnabled += (_, _) => enabled.TrySetResult();
        hook.KeyPressed += OnKeyPressed;
        hook.KeyReleased += OnKeyReleased;
        var started = new StartedHook(hook, hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true), enabled.Task, native.Ended);
        _hook = started;
        return started;
    }

    /// <summary>
    /// Waits for the hook to enable. One that has not enabled when the time is up is asked to stop, and what it does
    /// tells why it has not:
    /// <list type="bullet">
    /// <item>Its run ends: it was installed but heard nothing. A new hook takes its place, once.</item>
    /// <item>Its run goes on: it is still starting, since a stop reaches only a native run that has begun. It is kept: it
    /// delivers the hotkeys if it enables later, and the next bindings wait on it again, since a second native run
    /// beside it would leave two hooks installed.</item>
    /// </list>
    /// </summary>
    private async Task WaitUntilEnabledAsync(StartedHook started, bool mayRetry, CancellationToken ct)
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
        else if (await StopsWhenAskedAsync(started).ConfigureAwait(false))
        {
            Abandon(started);
            if (mayRetry)
            {
                log.Warn($"The keyboard hook did not enable in time and stopped when asked; starting a new one ({backend})");
                var next = await StartAsync(ct).ConfigureAwait(false);
                await WaitUntilEnabledAsync(next, mayRetry: false, ct).ConfigureAwait(false);
                return;
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new HotkeyUnavailableException("The keyboard hook did not start in time.");
    }

    /// <summary>
    /// Asks a hook that has not enabled to stop, and says whether its run ended. Stop, not Dispose, so that a hook
    /// still starting, which the stop does not reach, can still be disposed later.
    /// </summary>
    private async Task<bool> StopsWhenAskedAsync(StartedHook started)
    {
        try
        {
            started.Hook.Stop();
        }
        catch (HookException ex)
        {
            log.Debug($"Stopping a keyboard hook that has not enabled failed: {ex.Result}");
        }

        return await EndsWithinAsync(started, StopTimeout, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the running hook and waits until its run has ended, so the next bindings start a new one, and only then
    /// forgets the modifiers it saw held. A hook still starting is kept: stopping it before its native run begins would
    /// not stop that run, and the next bindings wait on it as before. With no bindings it raises nothing meanwhile.
    /// </summary>
    private async Task StopAsync()
    {
        if (_hook is not { Enabled.IsCompleted: true } started)
        {
            return;
        }

        _hook = null;
        var ended = await LetGoAsync(started).ConfigureAwait(false);

        // The run's thread can be in a handler until the run has ended: a key-down that reached the hook as it stopped,
        // or one whose handler was already running when its handlers were removed. Only now does none run any more.
        started.Hook.KeyPressed -= OnKeyPressed;
        started.Hook.KeyReleased -= OnKeyReleased;
        lock (_gate)
        {
            // Without the hook no key-up arrives, so a modifier seen going down would otherwise stay held.
            _held = HotkeyModifiers.None;
        }

        if (ended)
        {
            log.Info($"Keyboard hook stopped ({backend})");
        }
    }

    /// <summary>
    /// Disposes the hook and waits for its run to end, but no longer than <see cref="StopTimeout"/>: a native stop that
    /// does not return must not hold Pause, and the registrations queued behind it, for ever. A run that has not ended by
    /// then is remembered, so no new hook starts beside it.
    /// </summary>
    /// <returns>False when the run had not ended in time.</returns>
    private async Task<bool> LetGoAsync(StartedHook started)
    {
        try
        {
            started.Hook.Dispose();
        }
        catch (HookException ex)
        {
            log.Debug($"Stopping the keyboard hook failed: {ex.Result}");
        }

        if (!await EndsWithinAsync(started, StopTimeout, CancellationToken.None).ConfigureAwait(false))
        {
            _ending = started;
            log.Warn($"The keyboard hook did not stop within {StopTimeout.TotalSeconds} s; going on without waiting for it ({backend})");
            return false;
        }

        if (started.Run.Exception?.InnerException is HookException hookError)
        {
            // A hook that stopped with an error has nothing left to release.
            log.Debug($"The keyboard hook stopped with an error: {hookError.Result}");
        }

        return true;
    }

    /// <summary>
    /// Drops a hook whose run ended, or that stopped when asked after not enabling, or whose start was cancelled, so the
    /// next bindings start a new one, once its run has ended.
    /// </summary>
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

        lock (_gate)
        {
            // A hook whose run ended after it enabled sends no more key-ups.
            _held = HotkeyModifiers.None;
        }

        if (!started.Ended.IsCompleted)
        {
            _ending = started;
        }
    }

    /// <summary>True once the hook's run has ended for good, false if it has not within <paramref name="timeout"/>.</summary>
    private static async Task<bool> EndsWithinAsync(StartedHook started, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await started.Ended.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
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
    /// <param name="Run">Completes when the native run returns, before the run's thread has quite finished.</param>
    /// <param name="Enabled">Completes once the hook is installed and delivers keys.</param>
    /// <param name="Ended">Completes when the run's thread has finished with libuiohook: see <see cref="RunTracker"/>.</param>
    private sealed record StartedHook(IGlobalHook Hook, Task Run, Task Enabled, Task Ended);

    /// <summary>
    /// Passes everything to the native hook and says when a run has ended for good. SharpHook completes a run's task
    /// before the run's thread clears libuiohook's dispatch procedure, which every hook in the process shares, so a hook
    /// started in that gap loses its procedure and never hears a key. Clearing it is the last thing the thread does.
    /// </summary>
    private sealed class RunTracker(IGlobalHookProvider native) : IGlobalHookProvider
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ended => _ended.Task;

        public bool KeyTypedEnabled
        {
            get => native.KeyTypedEnabled;
            set => native.KeyTypedEnabled = value;
        }

        public void SetDispatchProc(DispatchProc? dispatchProc, nint userData)
        {
            try
            {
                native.SetDispatchProc(dispatchProc, userData);
            }
            finally
            {
                if (dispatchProc is null)
                {
                    _ended.TrySetResult();
                }
            }
        }

        public UioHookResult Run() => native.Run();

        public UioHookResult RunKeyboard() => native.RunKeyboard();

        public UioHookResult RunMouse() => native.RunMouse();

        public UioHookResult Stop() => native.Stop();
    }
}
