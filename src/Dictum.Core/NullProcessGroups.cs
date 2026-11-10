using System.Diagnostics;

namespace Dictum.Core;

/// <summary>Where the system has no group that ends together: the process is left as it is, and so is what it starts.</summary>
public sealed class NullProcessGroups : IProcessGroups
{
    public static NullProcessGroups Instance { get; } = new();

    public void Prepare(ProcessStartInfo start)
    {
    }

    public IDisposable Enclose(Process process) => Nothing.Instance;

    private sealed class Nothing : IDisposable
    {
        public static Nothing Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
