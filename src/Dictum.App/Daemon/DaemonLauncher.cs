using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Dictum.App.Resources;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Daemon;

/// <summary>A <c>dictum daemon</c> this app started.</summary>
public interface IDaemonProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    Task<int> Exited { get; }
}

public interface IDaemonLauncher
{
    /// <exception cref="DaemonLaunchException">The executable is missing or the OS refused to start it.</exception>
    IDaemonProcess Launch();

    /// <summary>
    /// Waits <paramref name="grace"/> for a daemon to exit, then kills it. For "Restart daemon" on a daemon from another
    /// version, which may not know <c>shutdown</c>. Refuses any process not named <c>dictum</c>.
    /// </summary>
    Task EndAsync(int processId, TimeSpan grace, CancellationToken ct);
}

public sealed class DaemonLaunchException(string message) : Exception(message);

/// <summary>Starts <c>dictum daemon</c> without a console window; its stdout and stderr go to the app's log.</summary>
/// <param name="environment">Variables set for the daemon on top of the app's own (tests point it at temp folders).</param>
public sealed class DaemonProcessLauncher(
    string executable, IReadOnlyList<string> arguments, ILog log, IReadOnlyDictionary<string, string>? environment = null) : IDaemonLauncher
{
    /// <summary><c>dictum</c> next to <c>dictum-gui</c>: both ship in one folder.</summary>
    public static string DefaultExecutable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "dictum.exe" : "dictum");

    public static DaemonProcessLauncher NextToApp(ILog log) => new(DefaultExecutable, ["daemon"], log);

    public IDaemonProcess Launch()
    {
        // Bare names ("cmd.exe", "/bin/sh") are left to the OS search path; a full path must exist.
        if (Path.IsPathFullyQualified(executable) && !File.Exists(executable))
        {
            throw new DaemonLaunchException(string.Format(CultureInfo.CurrentCulture, Strings.ProblemExecutableMissing, executable));
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);
        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new DaemonLaunchException(string.Format(CultureInfo.CurrentCulture, Strings.ProblemCannotStart, executable, ex.Message));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new Started(process, exited.Task);
    }

    public async Task EndAsync(int processId, TimeSpan grace, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (!IsDictum(process))
            {
                log.Warn($"Process {processId} is not a Dictum daemon; leaving it alone");
                return;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(grace);
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            log.Warn($"The daemon (pid {processId}) did not stop; ending it");
            process.Kill();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool IsDictum(Process process)
    {
        try
        {
            // Exactly "dictum": the test runner is "Dictum.App.Tests", and dictum-gui must never end itself.
            return string.Equals(process.ProcessName, "dictum", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            // It exited between the lookup and here.
            return false;
        }
    }

    private void Forward(string? line)
    {
        if (line is { Length: > 0 })
        {
            log.Info($"daemon: {line}");
        }
    }

    private sealed class Started(Process process, Task<int> exited) : IDaemonProcess
    {
        public int Id { get; } = process.Id;

        public Task<int> Exited => exited;

        /// <summary>Releases the handle; the daemon keeps running.</summary>
        public void Dispose() => process.Dispose();
    }
}
