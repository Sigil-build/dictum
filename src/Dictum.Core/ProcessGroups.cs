using System.ComponentModel;
using System.Diagnostics;

namespace Dictum.Core;

/// <summary>
/// Groups of processes that end together: a program Dictum starts, with every process that program starts in turn.
/// Windows has them as job objects; <see cref="NullProcessGroups"/> stands in on other systems.
/// </summary>
public interface IProcessGroups
{
    /// <summary>
    /// Puts <paramref name="process"/>, which has just started, in a group of its own, which every process it starts from
    /// then on joins. Disposing the group kills whatever of it still runs, and so does the end of Dictum's own process.
    /// </summary>
    /// <exception cref="Win32Exception">The system would not make the group, or put the process in it.</exception>
    IDisposable Enclose(Process process);
}

/// <summary>Where the system has no group that ends together: the process is left as it is, and so is what it starts.</summary>
public sealed class NullProcessGroups : IProcessGroups
{
    public static NullProcessGroups Instance { get; } = new();

    public IDisposable Enclose(Process process) => Nothing.Instance;

    private sealed class Nothing : IDisposable
    {
        public static Nothing Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
