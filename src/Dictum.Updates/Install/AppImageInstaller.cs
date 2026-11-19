namespace Dictum.Updates.Install;

/// <summary>
/// Replaces an AppImage with the downloaded one by a rename on its own filesystem, while Dictum still runs: the running
/// copy stays mounted from the old file, and a failed rename leaves the old AppImage and quits nothing. A runner then
/// waits for Dictum to exit, asks anything else of it to quit (a desktop app left running when <c>dictum update</c>
/// ran), and starts the new AppImage.
/// </summary>
public sealed class AppImageInstaller(IRunnerLauncher launcher, PendingUpdateFile pending) : IUpdateInstaller
{
    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public InstallKind Kind => InstallKind.AppImage;

    public void Start(DownloadedUpdate update, int waitForProcess, RestartTarget restart)
    {
        ArgumentNullException.ThrowIfNull(update);
        var appImage = Runner.Target(update, InstallKind.AppImage);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(update.PackageFile, Executable);
            }

            File.Move(update.PackageFile, appImage, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailure.CannotInstall, $"{appImage} could not be replaced: {ex.Message}", ex);
        }

        var (program, argument) = restart switch
        {
            RestartTarget.DesktopApp => (appImage, null),
            RestartTarget.Daemon => (appImage, "dictum daemon"),
            _ => ((string?)null, (string?)null),
        };
        var script = ShellScript.Begin(Runner.Log(update), waitForProcess)
            .Append(ShellScript.Quote(appImage)).Append(" --quit\n")
            .Restart(program, argument);
        Runner.Start(launcher, pending, update, ShellScript.Start(script.ToString()));
    }
}
