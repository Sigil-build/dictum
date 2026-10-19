namespace Dictum.Testing;

/// <summary>A new folder under the system temp folder, deleted with what it holds on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Short, so a socket inside the folder stays under the 100-byte socket path limit on every OS.</summary>
    private const int NameLength = 8;

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dictum-tests", Guid.NewGuid().ToString("N")[..NameLength]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file still open or a socket still bound; a folder left behind in temp does no harm.
        }
    }
}
