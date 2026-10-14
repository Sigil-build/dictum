namespace Dictum.Setup;

/// <summary>Reports on the calling thread, in order; <see cref="Progress{T}"/> posts asynchronously and can reorder.</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
