using System.ComponentModel;
using System.Diagnostics;

namespace Dictum.Updates.Install;

/// <summary>What the runner starts again once the installer is done: what ran before the update.</summary>
public enum RestartTarget
{
    None,
    DesktopApp,
    Daemon,
}

/// <summary>Installs a verified package of one install kind, through a runner that outlives Dictum.</summary>
public interface IUpdateInstaller
{
    InstallKind Kind { get; }

    /// <summary>
    /// Writes <c>pending.json</c>, then starts the runner, which waits for <paramref name="waitForProcess"/> to exit before
    /// it touches anything. The caller then exits: the desktop app quits with its daemon, the CLI returns. Throws
    /// <see cref="UpdateException"/> when the runner cannot start; then no record is left.
    /// </summary>
    void Start(DownloadedUpdate update, int waitForProcess, RestartTarget restart);
}

/// <summary>Starts a runner process; a fake in tests.</summary>
public interface IRunnerLauncher
{
    void Start(ProcessStartInfo start);
}

/// <summary>
/// Starts the runner on its own: without a window on Windows, in a session of its own through <c>setsid</c> on Linux, so
/// closing the terminal that ran <c>dictum update</c> does not end it.
/// </summary>
public sealed class DetachedRunner : IRunnerLauncher
{
    /// <summary>Where util-linux puts setsid; a system that keeps it elsewhere has it in a folder on PATH.</summary>
    private static readonly string[] SetsidPlaces = ["/usr/bin/setsid", "/bin/setsid"];

    private const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private const int NoSuchFile = 2;

    public void Start(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        if (OperatingSystem.IsLinux())
        {
            start.ArgumentList.Insert(0, start.FileName);
            start.FileName = FindSetsid(Environment.GetEnvironmentVariable("PATH"), CanRun)
                ?? throw new Win32Exception(NoSuchFile, $"setsid was not found in {string.Join(" or ", SetsidPlaces)} or on PATH");
        }

        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// setsid by its full path: util-linux's places first, then the folders on <paramref name="path"/>. A relative folder
    /// is skipped, so the runner never starts whatever setsid the current directory holds.
    /// </summary>
    internal static string? FindSetsid(string? path, Func<string, bool> canRun) =>
        SetsidPlaces
            .Concat((path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(Path.IsPathFullyQualified)
                .Select(folder => Path.Combine(folder, "setsid")))
            .FirstOrDefault(canRun);

    private static bool CanRun(string path) =>
        !OperatingSystem.IsWindows() && File.Exists(path) && (File.GetUnixFileMode(path) & Executable) != 0;
}

/// <summary>Shared steps of the installers: the record first, then the runner, and no record when the runner fails.</summary>
internal static class Runner
{
    public static void Start(IRunnerLauncher launcher, PendingUpdateFile pending, DownloadedUpdate update, ProcessStartInfo start)
    {
        try
        {
            var exitCode = Path.Combine(update.Folder, PendingUpdateFile.ExitCodeFile);
            if (File.Exists(exitCode))
            {
                File.Delete(exitCode);
            }

            pending.Save(new PendingUpdate
            {
                Version = update.Offer.Version.ToString(),
                Kind = update.Offer.Installation.Kind,
                Log = update.InstallLog,
                Started = DateTimeOffset.Now,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full or read-only data folder: without the record the next start could not report the update.
            throw new UpdateException(UpdateFailure.CannotInstall, $"The update's record could not be written: {ex.Message}", ex);
        }

        try
        {
            launcher.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            pending.Discard();
            throw new UpdateException(UpdateFailure.CannotInstall, $"The installer could not be started: {ex.Message}", ex);
        }
    }

    public static string Target(DownloadedUpdate update, InstallKind kind) =>
        update.Offer.Installation is { Target: { } target } installation && installation.Kind == kind
            ? target
            : throw new UpdateException(UpdateFailure.CannotInstall, $"This is not an {kind} installation.");
}
