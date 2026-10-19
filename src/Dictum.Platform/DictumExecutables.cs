namespace Dictum.Platform;

/// <summary>The names of Dictum's two programs, which are also their process names, and the CLI word that runs the daemon.</summary>
public static class DictumExecutables
{
    /// <summary>The CLI, which also runs the daemon.</summary>
    public const string Cli = "dictum";

    public const string DesktopApp = "dictum-gui";

    /// <summary>The CLI command that runs the daemon.</summary>
    public const string DaemonVerb = "daemon";

    /// <summary>The file of program <paramref name="name"/> on this OS: with <c>.exe</c> on Windows.</summary>
    public static string FileName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}
