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
    private static readonly TimeSpan ShortStart = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongStart = TimeSpan.FromMinutes(1);
    private static readonly HotkeyBinding[] Bindings = [new("dictate", Hotkey.Parse("Ctrl+Alt+Space"), "Dictate")];

    [Fact(Timeout = TestTimeout.Default)]
    public async Task AHookThatDoesNotStartInTimeIsDisposedAndTheNextBindingsStartANewOne()
    {
        var stuck = new SimpleGlobalHook(new FakeHookProvider(enables: false));
        var working = new FakeHookProvider(enables: true);
        var hooks = new Queue<SimpleGlobalHook>([stuck, new SimpleGlobalHook(working)]);
        await using var service = new SharpHookHotkeyService(NullLog.Instance, "test", hooks.Dequeue, ShortStart);

        var error = await Assert.ThrowsAsync<HotkeyUnavailableException>(() => service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken));
        await service.SetBindingsAsync(Bindings, TestContext.Current.CancellationToken);

        Assert.Contains("did not start in time", error.Message);
        Assert.True(stuck.IsDisposed);
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
    /// Stands in for libuiohook. A run fails at once with <paramref name="result"/>, or blocks until stopped; an enabling
    /// run raises HookEnabled first, as the real hook does once it is installed.
    /// </summary>
    private sealed class FakeHookProvider(bool enables, UioHookResult result = UioHookResult.Success) : IGlobalHookProvider
    {
        private static readonly TimeSpan LongestRun = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DispatchProc? _dispatch;
        private nint _userData;
        private volatile bool _enabled;

        public bool Enabled => _enabled;

        public bool KeyTypedEnabled { get; set; }

        public void SetDispatchProc(DispatchProc? dispatchProc, nint userData)
        {
            _dispatch = dispatchProc;
            _userData = userData;
        }

        public UioHookResult Run() => RunKeyboard();

        public UioHookResult RunMouse() => RunKeyboard();

        public UioHookResult RunKeyboard()
        {
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

            // A stuck hook that is never stopped ends on its own, so its thread does not outlive the test run.
            _stopped.Task.Wait(LongestRun);
            return UioHookResult.Success;
        }

        public UioHookResult Stop()
        {
            _stopped.TrySetResult();
            return UioHookResult.Success;
        }
    }
}
