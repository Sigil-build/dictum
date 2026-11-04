using System.Diagnostics;

namespace Dictum.Testing;

/// <summary>
/// Links to folders that a test makes: a symbolic link, or on a Windows account that may not make one, a junction,
/// which needs no right. Disposed before the test's temp folder, since Windows cannot remove a folder that holds a
/// junction along with it.
/// </summary>
public sealed class FolderLinks : IDisposable
{
    private readonly List<string> _links = [];

    public void Add(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            _links.Add(link);
            return;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // A required privilege is not held by the client.
        }

        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in (string[])["/c", "mklink", "/J", link, target])
        {
            start.ArgumentList.Add(argument);
        }

        using var mklink = Process.Start(start)!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        if (mklink.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new IOException($"mklink /J {link} {target} failed with code {mklink.ExitCode}");
        }

        _links.Add(link);
    }

    /// <summary>Removes a link made here, never what it points to.</summary>
    public void Remove(string link)
    {
        Delete(link);
        _links.Remove(link);
    }

    /// <summary>Removes each link, never what it points to.</summary>
    public void Dispose()
    {
        foreach (var link in _links)
        {
            Delete(link);
        }
    }

    private static void Delete(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(link);
        }
        else
        {
            // A symbolic link is a file to unlink on Linux, whatever it points to.
            File.Delete(link);
        }
    }
}
