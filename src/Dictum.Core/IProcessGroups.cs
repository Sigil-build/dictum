using System.ComponentModel;
using System.Diagnostics;

namespace Dictum.Core;

/// <summary>
/// Groups of processes that end together: a program Dictum starts, with every process that program starts in turn.
/// Windows has them as job objects, Linux as sessions started through setsid; <see cref="NullProcessGroups"/> stands in
/// on other systems.
/// </summary>
public interface IProcessGroups
{
    /// <summary>
    /// Readies the start of a program that <see cref="Enclose"/> will take, before it starts: on Linux a process can no
    /// longer change its group once it runs its program, so it starts through setsid.
    /// </summary>
    void Prepare(ProcessStartInfo start);

    /// <summary>
    /// Puts <paramref name="process"/>, which has just started from a start that <see cref="Prepare"/> readied, in a group
    /// of its own, which every process it starts from then on joins. Disposing the group kills whatever of it still runs.
    /// On Windows, so does the end of Dictum's own process.
    /// </summary>
    /// <exception cref="Win32Exception">The system would not make the group, or put the process in it.</exception>
    IDisposable Enclose(Process process);
}
