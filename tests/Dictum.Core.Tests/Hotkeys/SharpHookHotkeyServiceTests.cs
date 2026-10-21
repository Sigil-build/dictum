using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Input;
using Dictum.Testing;
using SharpHook.Data;
using SharpHook.Providers;

namespace Dictum.Core.Tests.Hotkeys;

public sealed class SharpHookHotkeyServiceTests
{
    /// <summary>Short for a test, yet with room for a working hook's thread to start and enable while other tests load the machine.</summary>
    private static readonly TimeSpan ShortStart = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LongStart = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Longer than the service waits for a hook to stop.</summary>
    private static readonly TimeSpan StopPatience = TimeSpan.FromSeconds(5);

    /// <summary>How long a paused run's thread is held up before its last step, as a busy machine can hold it.</summary>
    private static readonly TimeSpan HeldUp = TimeSpan.FromMilliseconds(300);

    private static readonly HotkeyBinding[] Bindings = [new("dictate", Hotkey.Parse("Ctrl+Alt+Space"), "Dictate")];

    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatEnablesAfterTheTimeoutDeliversHotkeysAndNoSecondHookIsCreated()
    {
        var late = new FakeHookProvider(enables: true, startsAtOnce: false);
        var providers = new Queue<IGlobalHookProvider>([late, new FakeHookProvider(enables: true)]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", providers.Dequeue, ShortStart);
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
        Assert.Single(providers);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task BindingsWhileAHookIsStillStartingWaitOnItAndReportItsFailure()
    {
        var slow = new FakeHookProvider(enables: false, UioHookResult.ErrorSetWindowsHookEx, startsAtOnce: false);
        var providers = new Queue<IGlobalHookProvider>([slow, new FakeHookProvider(enables: true)]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", providers.Dequeue, ShortStart);
        await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));

        var waiting = service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        slow.Start();
        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => waiting);

        Assert.Contains("failed to start", error.Message);
        Assert.Single(providers);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatDoesNotStartInTimeIsKeptUntilItsRunEndsAndThenTheNextBindingsStartANewOne()
    {
        var slow = new FakeHookProvider(enables: false, UioHookResult.ErrorSetWindowsHookEx, startsAtOnce: false);
        var working = new FakeHookProvider(enables: true);
        var providers = new Queue<IGlobalHookProvider>([slow, working]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", providers.Dequeue, ShortStart);

        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));
        var keptAfterTheTimeout = providers.Count == 1;
        slow.Start();
        await Eventually.WaitForAsync(() => slow.Ended, "the slow hook's run to end");
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);

        Assert.Contains("did not start in time", error.Message);
        Assert.True(keptAfterTheTimeout);
        Assert.True(working.Enabled);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatFailsToStartIsTriedAgainWithTheNextBindings()
    {
        var working = new FakeHookProvider(enables: true);
        var providers = new Queue<IGlobalHookProvider>([new FakeHookProvider(enables: false, UioHookResult.ErrorSetWindowsHookEx), working]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", providers.Dequeue, LongStart);

        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);

        Assert.Contains("failed to start", error.Message);
        Assert.True(working.Enabled);
    }

    // Pause all hotkeys sets no bindings: the hook must let go of the keyboard then, and Resume must bring the hotkeys back.
    [Fact(Timeout = TestTimeout.Default)]
    public async Task NoBindingsStopTheHookAndTheNextBindingsStartANewOneThatDelivers()
    {
        var paused = new FakeHookProvider(enables: true);
        var resumed = new FakeHookProvider(enables: true);
        var providers = new Queue<IGlobalHookProvider>([paused, resumed]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", providers.Dequeue, LongStart);
        var pressed = new TaskCompletionSource<HotkeyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Triggered += e => pressed.TrySetResult(e);

        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        var ranBeforeThePause = paused.Running;
        await service.SetBindingsAsync([], TestContext.Current.CancellationToken);
        var (runningWhilePaused, stoppedWhilePaused) = (paused.Running, paused.StopRequested);
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        resumed.Press(KeyCode.VcLeftControl);
        resumed.Press(KeyCode.VcLeftAlt);
        resumed.Press(KeyCode.VcSpace);

        Assert.True(ranBeforeThePause);
        Assert.False(runningWhilePaused);
        Assert.True(stoppedWhilePaused);
        Assert.True(resumed.Enabled);
        Assert.Equal(new HotkeyEvent("dictate", HotkeyPhase.Pressed), await pressed.Task.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken));
        Assert.Empty(providers);
    }

    // SharpHook completes a run's task before the run's thread clears libuiohook's dispatch procedure, which every hook in
    // the process shares. A hook started in between loses its procedure to that clear and never hears a key.
    [Fact(Timeout = TestTimeout.Default)]
    public async Task AResumeRightAfterAPauseWaitsForThePausedRunToEndAndDelivers()
    {
        var dispatcher = new FakeDispatcher();
        var paused = new FakeHookProvider(enables: true, dispatcher: dispatcher, clearsAfter: HeldUp);
        var resumed = new FakeHookProvider(enables: true, dispatcher: dispatcher);
        var providers = new Queue<IGlobalHookProvider>([paused, resumed]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", providers.Dequeue, LongStart);
        var pressed = new TaskCompletionSource<HotkeyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Triggered += e => pressed.TrySetResult(e);

        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        await service.SetBindingsAsync([], TestContext.Current.CancellationToken);
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        await Eventually.WaitForAsync(() => paused.Ended, "the paused run to end");
        resumed.Press(KeyCode.VcLeftControl);
        resumed.Press(KeyCode.VcLeftAlt);
        resumed.Press(KeyCode.VcSpace);

        Assert.Equal(new HotkeyEvent("dictate", HotkeyPhase.Pressed), await pressed.Task.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken));
    }

    // Installed but never enabled, a hook hears nothing; kept, it would leave every hotkey dead until the daemon restarts.
    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatRunsButNeverEnablesIsStoppedAndANewOneTakesItsPlace()
    {
        var deaf = new FakeHookProvider(enables: false);
        var working = new FakeHookProvider(enables: true);
        var providers = new Queue<IGlobalHookProvider>([deaf, working]);
        var log = new ListLog();
        await using var service = new SharpHookHotkeyService(log, "test", providers.Dequeue, ShortStart);
        var pressed = new TaskCompletionSource<HotkeyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Triggered += e => pressed.TrySetResult(e);

        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        working.Press(KeyCode.VcLeftControl);
        working.Press(KeyCode.VcLeftAlt);
        working.Press(KeyCode.VcSpace);

        Assert.Equal(new HotkeyEvent("dictate", HotkeyPhase.Pressed), await pressed.Task.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken));
        Assert.True(deaf.Ended);
        Assert.Empty(providers);
        Assert.True(log.Has("starting a new one"));
    }

    // A native stop that never returns must not hold Pause, and with it every later registration, for ever.
    [Fact(Timeout = TestTimeout.Default)]
    public async Task APauseWhoseHookDoesNotStopGoesOnAndNoSecondHookStartsBesideIt()
    {
        var stuck = new FakeHookProvider(enables: true, endsOnStop: false);
        var next = new FakeHookProvider(enables: true);
        var providers = new Queue<IGlobalHookProvider>([stuck, next]);
        var log = new ListLog();
        await using var service = new SharpHookHotkeyService(log, "test", providers.Dequeue, LongStart);

        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);
        await service.SetBindingsAsync([], TestContext.Current.CancellationToken).WaitAsync(StopPatience, TestContext.Current.CancellationToken);
        var whileStuck = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));
        var startedBesideIt = providers.Count == 0;
        stuck.Release();
        await Eventually.WaitForAsync(() => stuck.Ended, "the stuck run to end");
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);

        Assert.Contains(log.Lines, l => l.Level == Diagnostics.LogLevel.Warning && l.Message.Contains("did not stop", StringComparison.Ordinal));
        Assert.Contains("has not stopped", whileStuck.Message);
        Assert.False(startedBesideIt);
        Assert.True(next.Enabled);
    }

    [Fact(Timeout = TestTimeout.Default)]
    public async Task ACancelledStartIsReportedAsCancelledAndStopsTheHook()
    {
        var hook = new FakeHookProvider(enables: false);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", () => hook, LongStart);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetBindingsAsync(Bindings, cancelled.Token));

        Assert.True(hook.StopRequested);
    }

    /// <summary>libuiohook's dispatch procedure: one for the whole process, set by whichever hook set it last.</summary>
    private sealed class FakeDispatcher
    {
        private volatile Slot? _slot;

        public void Set(DispatchProc? dispatchProc, nint userData) => _slot = new Slot(dispatchProc, userData);

        public void Dispatch(ref UioHookEvent hookEvent)
        {
            if (_slot is { Proc: { } proc } slot)
            {
                proc(ref hookEvent, slot.UserData);
            }
        }

        private sealed record Slot(DispatchProc? Proc, nint UserData);
    }

    /// <summary>
    /// Stands in for libuiohook. A run fails with <paramref name="result"/>, or blocks until stopped; an enabling run
    /// raises HookEnabled first, as the real hook does once it is installed. A run that does not start at once waits for
    /// <see cref="Start"/>, as a hook still loading its native library does, and a stop before then is lost, as
    /// libuiohook's is. A run that does not end on a stop waits for <see cref="Release"/> after it, as a native stop
    /// that does not return would. Hooks that share a <paramref name="dispatcher"/> share one dispatch procedure, as
    /// every hook in a process shares libuiohook's.
    /// </summary>
    /// <param name="clearsAfter">How long the run's thread is held up before it clears the dispatch procedure.</param>
    private sealed class FakeHookProvider(
        bool enables,
        UioHookResult result = UioHookResult.Success,
        bool startsAtOnce = true,
        bool endsOnStop = true,
        FakeDispatcher? dispatcher = null,
        TimeSpan clearsAfter = default) : IGlobalHookProvider
    {
        private static readonly TimeSpan LongestRun = TimeSpan.FromSeconds(30);

        private readonly FakeDispatcher _dispatcher = dispatcher ?? new FakeDispatcher();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _running;
        private volatile bool _enabled;
        private volatile bool _ended;
        private volatile bool _stopRequested;

        public bool Enabled => _enabled;

        /// <summary>Its native run has begun and has not returned.</summary>
        public bool Running => _running;

        /// <summary>SharpHook cleared the dispatch procedure, the last thing a run's thread does.</summary>
        public bool Ended => _ended;

        public bool StopRequested => _stopRequested;

        /// <summary>A key that goes down as the hook stops: the run's thread delivers it before the run returns.</summary>
        public KeyCode? KeyDownWhileStopping { get; init; }

        public bool KeyTypedEnabled { get; set; }

        public void Start() => _started.TrySetResult();

        public void Release() => _released.TrySetResult();

        public void Press(KeyCode code) => Dispatch(EventType.KeyPressed, code);

        public void SetDispatchProc(DispatchProc? dispatchProc, nint userData)
        {
            if (dispatchProc is null && clearsAfter > TimeSpan.Zero)
            {
                Thread.Sleep(clearsAfter);
            }

            _dispatcher.Set(dispatchProc, userData);
            _ended = dispatchProc is null;
        }

        public UioHookResult Run() => RunKeyboard();

        public UioHookResult RunMouse() => RunKeyboard();

        public UioHookResult RunKeyboard()
        {
            // A run that is never started, stopped or released ends on its own, so its thread does not outlive the test run.
            if (!startsAtOnce)
            {
                _started.Task.Wait(LongestRun);
            }

            if (result != UioHookResult.Success)
            {
                return result;
            }

            _running = true;
            if (enables)
            {
                _enabled = true;
                var hookEnabled = new UioHookEvent { Type = EventType.HookEnabled };
                _dispatcher.Dispatch(ref hookEnabled);
            }

            _stopped.Task.Wait(LongestRun);
            if (!endsOnStop)
            {
                _released.Task.Wait(LongestRun);
            }

            if (KeyDownWhileStopping is { } key)
            {
                Dispatch(EventType.KeyPressed, key);
            }

            _running = false;
            return UioHookResult.Success;
        }

        public UioHookResult Stop()
        {
            _stopRequested = true;
            if (!_running)
            {
                return UioHookResult.Failure;
            }

            _stopped.TrySetResult();
            return UioHookResult.Success;
        }

        private void Dispatch(EventType type, KeyCode code)
        {
            var hookEvent = new UioHookEvent { Type = type, Keyboard = new KeyboardEventData { KeyCode = code } };
            _dispatcher.Dispatch(ref hookEvent);
        }
    }
}
