using System.ComponentModel;
using System.Diagnostics;

namespace Dictum.App.Shell;

/// <summary>The Dictum processes of one install folder: what <c>--quit</c> stops and waits for (phase 4 PR 4b final review I2).</summary>
public interface IDictumProcesses
{
    /// <summary>The ids of <c>dictum</c> and <c>dictum-gui</c> processes whose executable lies in <paramref name="folder"/>, this process excluded.</summary>
    IReadOnlyList<int> RunningFrom(string folder);
}

/// <summary>The real processes, by name and executable path.</summary>
public sealed class SystemDictumProcesses : IDictumProcesses
{
    public static readonly SystemDictumProcesses Instance = new();

    public IReadOnlyList<int> RunningFrom(string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var found = new List<int>();
        foreach (var name in new[] { "dictum", "dictum-gui" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId && process.MainModule?.FileName is { } image && image.StartsWith(root, comparison))
                        {
                            found.Add(process.Id);
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Gone meanwhile, or another user's: not ours to wait for.
                    }
                }
            }
        }

        return found;
    }
}
