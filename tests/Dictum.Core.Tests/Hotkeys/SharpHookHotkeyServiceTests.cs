using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Input;
using Dictum.Testing;
using SharpHook;
using SharpHook.Data;
using SharpHook.Providers;

namespace Dictum.Core.Tests.Hotkeys;

public sealed class SharpHookHotkeyServiceTests
{
    /// <summary>Short for a test, yet with room for a working hook's thread to start and enable while other tests load the machine.</summary>
    private static readonly TimeSpan ShortStart = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LongStart = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly HotkeyBinding[] Bindings = [new("dictate", Hotkey.Parse("Ctrl+Alt+Space"), "Dictate")];

    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatEnablesAfterTheTimeoutDeliversHotkeysAndNoSecondHookIsCreated()
    {
        var late = new FakeHookProvider(enables: true, startsAtOnce: false);
        var hooks = new Queue<SimpleGlobalHook>([new SimpleGlobalHook(late), new SimpleGlobalHook(new FakeHookProvider(enables: true))]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", hooks.Dequeue, ShortStart);
        var pressed = new TaskCompletionSource<HotkeyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Triggered += e => pressed.TrySetResult(e);

        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));
        late.Start();
        await Eventually.WaitForAsync(() => late.Enabled, "the late hook to enable");
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        late.Press(KeyCode.VcLeftControl);
        late.Press(KeyCode.VcLeftAlt);
        late.Press(KeyCode.VcSpace);

        Assert.Contains("did not start in time", error.Message);
        Assert.Equal(new HotkeyEvent("dictate", HotkeyPhase.Pressed), await pressed.Task.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken));
        Assert.Single(hooks);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task BindingsWhileAHookIsStillStartingWaitOnItAndReportItsFailure()
    {
        var slow = new FakeHookProvider(enables: false, UioHookResult.ErrorSetWindowsHookEx, startsAtOnce: false);
        var hooks = new Queue<SimpleGlobalHook>([new SimpleGlobalHook(slow), new SimpleGlobalHook(new FakeHookProvider(enables: true))]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", hooks.Dequeue, ShortStart);
        await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));

        var waiting = service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        slow.Start();
        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => waiting);

        Assert.Contains("failed to start", error.Message);
        Assert.Single(hooks);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatDoesNotStartInTimeIsKeptUntilItsRunEndsAndThenTheNextBindingsStartANewOne()
    {
        var slow = new FakeHookProvider(enables: false, UioHookResult.ErrorSetWindowsHookEx, startsAtOnce: false);
        var slowHook = new SimpleGlobalHook(slow);
        var working = new FakeHookProvider(enables: true);
        var hooks = new Queue<SimpleGlobalHook>([slowHook, new SimpleGlobalHook(working)]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", hooks.Dequeue, ShortStart);

        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));
        var keptAfterTheTimeout = !slowHook.IsDisposed;
        slow.Start();
        await Eventually.WaitForAsync(() => slow.Ended, "the slow hook's run to end");
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);

        Assert.Contains("did not start in time", error.Message);
        Assert.True(keptAfterTheTimeout);
        Assert.True(slowHook.IsDisposed);
        Assert.True(working.Enabled);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatFailsToStartIsTriedAgainWithTheNextBindings()
    {
        var working = new FakeHookProvider(enables: true);
        var hooks = new Queue<SimpleGlobalHook>(
            [new SimpleGlobalHook(new FakeHookProvider(enables: false, UioHookResult.ErrorSetWindowsHookEx)), new SimpleGlobalHook(working)]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", hooks.Dequeue, LongStart);

        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);

        Assert.Contains("failed to start", error.Message);
        Assert.True(working.Enabled);
    }

    // Pause all hotkeys sets no bindings: the hook must let go of the keyboard then, and Resume must bring the hotkeys back.
    [Fact(Timeout = TestTimeout.Default)]
    public async Task NoBindingsStopTheHookAndTheNextBindingsStartANewOneThatDelivers()
    {
        var paused = new SimpleGlobalHook(new FakeHookProvider(enables: true));
        var resumed = new FakeHookProvider(enables: true);
        var hooks = new Queue<SimpleGlobalHook>([paused, new SimpleGlobalHook(resumed)]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", hooks.Dequeue, LongStart);
        var pressed = new TaskCompletionSource<HotkeyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Triggered += e => pressed.TrySetResult(e);

        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        var ranBeforeThePause = paused.IsRunning;
        await service.SetBindingsAsync([], TestContext.Current.CancellationToken);
        var (runningWhilePaused, disposedWhilePaused) = (paused.IsRunning, paused.IsDisposed);
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        resumed.Press(KeyCode.VcLeftControl);
        resumed.Press(KeyCode.VcLeftAlt);
        resumed.Press(KeyCode.VcSpace);

        Assert.True(ranBeforeThePause);
        Assert.False(runningWhilePaused);
        Assert.True(disposedWhilePaused);
        Assert.True(resumed.Enabled);
        Assert.Equal(new HotkeyEvent("dictate", HotkeyPhase.Pressed), await pressed.Task.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken));
        Assert.Empty(hooks);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task ACancelledStartIsReportedAsCancelledAndDisposesTheHook()
    {
        var hook = new SimpleGlobalHook(new FakeHookProvider(enables: false));
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", () => hook, LongStart);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetBindingsAsync(Bindings, cancelled.Token));

        Assert.True(hook.IsDisposed);
    }

    /// <summary>
    /// Stands in for libuiohook. A run fails with <paramref name="result"/>, or blocks until stopped; an enabling run
    /// raises HookEnabled first, as the real hook does once it is installed. A run that does not start at once waits for
    /// <see cref="Start"/>, as a hook still loading its native library does.
    /// </summary>
    private sealed class FakeHookProvider(bool enables, UioHookResult result = UioHookResult.Success, bool startsAtOnce = true) : IGlobalHookProvider
    {
        private static readonly TimeSpan LongestRun = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile DispatchProc? _dispatch;
        private nint _userData;
        private volatile bool _enabled;
        private volatile bool _ended;

        public bool Enabled => _enabled;

        /// <summary>SharpHook cleared the dispatch procedure, which it does once the run's task has completed.</summary>
        public bool Ended => _ended;

        public bool KeyTypedEnabled { get; set; }

        public void Start() => _started.TrySetResult();

        public void Press(KeyCode code) => Dispatch(EventType.KeyPressed, code);

        public void SetDispatchProc(DispatchProc? dispatchProc, nint userData)
        {
            _userData = userData;
            _dispatch = dispatchProc;
            _ended = dispatchProc is null;
        }

        public UioHookResult Run() => RunKeyboard();

        public UioHookResult RunMouse() => RunKeyboard();

        public UioHookResult RunKeyboard()
        {
            // A run that is never started or stopped ends on its own, so its thread does not outlive the test run.
            if (!startsAtOnce)
            {
                _started.Task.Wait(LongestRun);
            }

            if (result != UioHookResult.Success)
            {
                return result;
            }

            if (enables)
            {
                _enabled = true;
                var hookEnabled = new UioHookEvent { Type = EventType.HookEnabled };
                _dispatch?.Invoke(ref hookEnabled, _userData);
            }

            _stopped.Task.Wait(LongestRun);
            return UioHookResult.Success;
        }

        public UioHookResult Stop()
        {
            _stopped.TrySetResult();
            return UioHookResult.Success;
        }

        private void Dispatch(EventType type, KeyCode code)
        {
            var hookEvent = new UioHookEvent { Type = type, Keyboard = new KeyboardEventData { KeyCode = code } };
            _dispatch?.Invoke(ref hookEvent, _userData);
        }
    }
}
