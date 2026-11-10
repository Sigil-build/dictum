using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dictum.Core;

namespace Dictum.Platform.Linux;

/// <summary>
/// A session for each group. The program starts through setsid, which runs it in the same process as the leader of a new
/// session, and of a process group whose id is its pid. Every process it starts joins that group, unless it starts a
/// session of its own. Disposing the group sends SIGKILL to all of it.
/// Unlike a job object, the group does not end when Dictum's own process is killed: the plugin still exits as its stdin
/// closes, but a process it started that ignores that may outlive Dictum.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class SessionProcessGroups(string? setsid) : IProcessGroups
{
    private const string LibC = "libc";
    private const int SigKill = 9;
    private const int NoSuchFile = 2;

    private const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Where util-linux puts setsid; a system that keeps it elsewhere has it on PATH.</summary>
    private static readonly string[] SetsidPlaces = ["/usr/bin/setsid", "/bin/setsid"];

    /// <summary>
    /// How long setsid may take to make the session once the process runs it: a moment, but longer on a busy system. The
    /// process starts as setsid, in Dictum's own group, and leaves it only when setsid has made the session.
    /// </summary>
    private static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(5);

    /// <summary>The groups of this system: through its setsid, or none when it has no setsid.</summary>
    public static SessionProcessGroups Create() => new(FindSetsid());

    /// <summary>
    /// Runs the program through setsid. A program that cannot run is left as it is, so that the system's own error names
    /// it; through setsid it would start, and only setsid would say that it could not run the program.
    /// </summary>
    public void Prepare(ProcessStartInfo start)
    {
        if (setsid is null || !CanRun(start.FileName))
        {
            return;
        }

        start.ArgumentList.Insert(0, start.FileName);
        start.FileName = setsid;
    }

    /// <summary>
    /// Takes the group that the process leads, waiting while it is still in Dictum's own group for setsid to make it.
    /// </summary>
    /// <exception cref="Win32Exception">There is no setsid, or the process does not lead a group of its own.</exception>
    public IDisposable Enclose(Process process)
    {
        if (setsid is null)
        {
            throw new Win32Exception(NoSuchFile, $"setsid was not found in {string.Join(" or ", SetsidPlaces)} or on PATH");
        }

        var own = GetOwnProcessGroup();
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var group = GetProcessGroup(process.Id);
            if (group < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (group == process.Id)
            {
                return new Group(group);
            }

            if (group != own || waited.Elapsed >= SessionWait)
            {
                throw new Win32Exception($"process {process.Id} is in group {group}, not one of its own");
            }

            Thread.Sleep(1);
        }
    }

    private static string? FindSetsid()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return SetsidPlaces
            .Concat(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(folder => Path.Combine(folder, "setsid")))
            .FirstOrDefault(CanRun);
    }

    private static bool CanRun(string path) =>
        Path.IsPathFullyQualified(path) && File.Exists(path) && (File.GetUnixFileMode(path) & Executable) != 0;

    [LibraryImport(LibC, EntryPoint = "getpgid", SetLastError = true)]
    private static partial int GetProcessGroup(int pid);

    [LibraryImport(LibC, EntryPoint = "getpgrp")]
    private static partial int GetOwnProcessGroup();

    [LibraryImport(LibC, EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    /// <summary>
    /// The process group, ended once. It ends right after its leader exits or is stopped, before its id is likely to go
    /// to another group. Nothing left of it is no error, and neither is a kill the system refuses: like closing a job
    /// object's handle, letting go of a group never throws.
    /// </summary>
    private sealed class Group(int id) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _ = Kill(-id, SigKill);
            }
        }
    }
}
