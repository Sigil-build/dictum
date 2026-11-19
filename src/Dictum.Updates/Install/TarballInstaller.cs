using System.Globalization;

namespace Dictum.Updates.Install;

/// <summary>
/// Updates a tarball install with the new package's own <c>install.sh</c>: the tarball is unpacked next to it, then a
/// runner waits for Dictum to exit and runs it. <c>install.sh</c> quits what still runs from the installed folder, swaps
/// the folder and puts the old one back when the swap fails, so the runner then starts whatever is installed. Its
/// exit status is written down, but the verdict is the version check on the next start.
/// </summary>
public sealed class TarballInstaller(IRunnerLauncher launcher, PendingUpdateFile pending) : IUpdateInstaller
{
    public InstallKind Kind => InstallKind.Tarball;

    public void Start(DownloadedUpdate update, int waitForProcess, RestartTarget restart)
    {
        ArgumentNullException.ThrowIfNull(update);
        var lib = Runner.Target(update, InstallKind.Tarball).TrimEnd('/');
        string root;
        try
        {
            root = SafeTar.Extract(update.PackageFile, update.Folder, ReleaseFiles.TarballRoot(update.Offer.Version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailure.CannotInstall, $"{update.PackageFile} could not be unpacked: {ex.Message}", ex);
        }

        var (program, argument) = restart switch
        {
            RestartTarget.DesktopApp => (lib + "/dictum-gui", null),
            RestartTarget.Daemon => (lib + "/dictum", "daemon"),
            _ => ((string?)null, (string?)null),
        };
        var exitCode = Path.Combine(update.Folder, PendingUpdateFile.ExitCodeFile);
        var script = ShellScript.Begin(Runner.Log(update), waitForProcess)
            .Append(ShellScript.Quote(Path.Combine(root, "install.sh"))).Append('\n')
            .Append(CultureInfo.InvariantCulture, $"echo $? > {ShellScript.Quote(exitCode)}\n")
            .Restart(program, argument);
        Runner.Start(launcher, pending, update, ShellScript.Start(script.ToString()));
    }
}
