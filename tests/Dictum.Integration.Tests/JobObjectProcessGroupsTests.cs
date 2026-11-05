using System.Diagnostics;
using Dictum.Platform.Windows;

namespace Dictum.Integration.Tests;

/// <summary>The real job object that holds a plugin and what it starts; skipped on other OSes.</summary>
public sealed class JobObjectProcessGroupsTests
{
    /// <summary>For the system to end a killed process, on a busy runner too.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AProcessInTheGroupIsKilledWhenTheGroupIsDisposed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        using var process = Waiting();
        try
        {
            var group = new JobObjectProcessGroups().Enclose(process);
            Assert.False(process.WaitForExit(TimeSpan.FromMilliseconds(300)), "the process ended before the group was disposed");

            group.Dispose();

            await process.WaitForExitAsync(ct).WaitAsync(Patience, ct);
        }
        finally
        {
            // Its input closes with it, so a process the group did not end ends here.
            process.StandardInput.Dispose();
        }
    }

    /// <summary>sort.exe reads its input to the end before it writes anything, so it runs until its input closes.</summary>
    private static Process Waiting() =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sort.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
}
