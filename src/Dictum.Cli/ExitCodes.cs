namespace Dictum.Cli;

/// <summary>The exit codes scripts can rely on.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int InvalidConfig = 2;
    public const int ProviderUnavailable = 3;
    public const int DaemonNotRunning = 4;
    public const int DownloadFailed = 5;

    /// <summary>128 + SIGINT, what shells report for Ctrl+C.</summary>
    public const int Interrupted = 130;
}
