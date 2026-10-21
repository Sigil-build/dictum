using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Input;
using Dictum.Testing;

namespace Dictum.Integration.Tests;

/// <summary>
/// The real low-level keyboard hook (libuiohook through SharpHook) as the daemon runs it on Windows; skipped on other
/// OSes and without a desktop session. The service is disposed at the end, so no hook stays installed.
/// </summary>
public sealed class KeyboardHookTests
{
    private const int Rounds = 6;

    /// <summary>A key no keyboard here has, so the hook never takes one from the person using the machine.</summary>
    private static readonly HotkeyBinding[] Bindings = [new("dictate", Hotkey.Parse("F24"), "Dictate")];

    // Pause all hotkeys sets no bindings and Resume sets them again. A resume that comes right after the pause must get
    // a hook that enables: SetBindingsAsync throws "did not start in time" for one that never does.
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AHookStartedRightAfterAPauseEnablesEveryTime()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            Assert.Skip("Windows with a desktop session only");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var log = new ListLog();
        var service = new SharpHookHotkeyService(log, "test");
        try
        {
            await service.SetBindingsAsync(Bindings, ct);
            for (var round = 0; round < Rounds; round++)
            {
                await service.SetBindingsAsync([], ct);
                await service.SetBindingsAsync(Bindings, ct);
            }
        }
        finally
        {
            await service.DisposeAsync();
        }

        Assert.Equal(Rounds + 1, log.Messages.Count(m => m.StartsWith("Keyboard hook running", StringComparison.Ordinal)));
        Assert.Equal(Rounds, log.Messages.Count(m => m.StartsWith("Keyboard hook stopped", StringComparison.Ordinal)));
        Assert.DoesNotContain(log.Lines, line => line.Level >= LogLevel.Warning);
    }
}
