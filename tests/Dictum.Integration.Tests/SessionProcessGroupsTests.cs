using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Dictum.Platform.Linux;

namespace Dictum.Integration.Tests;

/// <summary>The real setsid session that holds a plugin and what it starts; skipped on other OSes.</summary>
/// <remarks>The platform analyzer does not take <c>Assert.SkipUnless</c> as a check, so the class says it.</remarks>
[SupportedOSPlatform("linux")]
public sealed class SessionProcessGroupsTests
{
    /// <summary>For the system to end a killed process, on a busy runner too.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A shell that starts a child and waits for it, as a plugin's launcher script does. The child joins the shell's
    /// group, and disposing the group kills both.
    /// </summary>
    [Fact]
    public async Task DisposingTheGroupKillsTheProcessAndWhatItStarted()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var ct = TestContext.Current.CancellationToken;
        var groups = SessionProcessGroups.Create();
        var start = Shell("sleep 600 & echo $!; wait");
        groups.Prepare(start);

        using var process = Process.Start(start)!;
        Process? child = null;
        try
        {
            var group = groups.Enclose(process);
            child = Process.GetProcessById(int.Parse((await process.StandardOutput.ReadLineAsync(ct))!, CultureInfo.InvariantCulture));
            Assert.False(child.WaitForExit(TimeSpan.FromMilliseconds(300)), "the child ended before the group was disposed");

            group.Dispose();

            await process.WaitForExitAsync(ct).WaitAsync(Patience, ct);
            await child.WaitForExitAsync(ct).WaitAsync(Patience, ct);
        }
        finally
        {
            Kill(child);
            Kill(process);
            child?.Dispose();
        }
    }

    /// <summary>Disposing a group twice, or once all of it has ended, is no error.</summary>
    [Fact]
    public async Task AGroupThatHasEndedCanBeDisposedAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var ct = TestContext.Current.CancellationToken;
        var groups = SessionProcessGroups.Create();
        var start = Shell("exit 0");
        groups.Prepare(start);

        using var process = Process.Start(start)!;
        var group = groups.Enclose(process);
        await process.WaitForExitAsync(ct).WaitAsync(Patience, ct);

        group.Dispose();
        group.Dispose();
    }

    /// <summary>
    /// A program that cannot run is started as it is, so that the system's own error names it: through setsid it would
    /// start, and only setsid would say that it could not run the program.
    /// </summary>
    [Fact]
    public void AProgramThatCannotRunIsLeftAsItIs()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var start = new ProcessStartInfo("/nonexistent/plugin");

        SessionProcessGroups.Create().Prepare(start);

        Assert.Equal("/nonexistent/plugin", start.FileName);
        Assert.Empty(start.ArgumentList);
    }

    /// <summary>Without setsid nothing changes the start, and the group is refused, which the plugin's log names.</summary>
    [Fact]
    public void WithoutSetsidTheGroupIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        var groups = new SessionProcessGroups(setsid: null);
        var start = Shell("exit 0");

        groups.Prepare(start);
        using var process = Process.Start(start)!;

        Assert.Equal("/bin/sh", start.FileName);
        var refusal = Assert.Throws<Win32Exception>(() => groups.Enclose(process));
        Assert.Contains("setsid", refusal.Message, StringComparison.Ordinal);
    }

    private static ProcessStartInfo Shell(string script)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return start;
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // It ended meanwhile.
        }
    }
}
