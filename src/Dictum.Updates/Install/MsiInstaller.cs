using System.Diagnostics;
using System.Globalization;

namespace Dictum.Updates.Install;

/// <summary>
/// Updates an MSI install with the newer MSI, so Windows Installer keeps knowing what is installed. A hidden
/// <c>cmd.exe</c> waits for Dictum to exit, runs <c>msiexec /i … /passive</c> (whose <c>QuitDictum</c> stops anything
/// left and whose <c>LaunchDictum</c> starts the new version), writes msiexec's exit code, and starts the old version
/// again when the MSI rolled back. 3010 means it installed but files in use wait for a restart of Windows.
/// </summary>
public sealed class MsiInstaller : IUpdateInstaller
{
    /// <summary>How long the runner waits for the process that started it; then msiexec's <c>QuitDictum</c> takes over.</summary>
    internal const int MaxWaitSeconds = 60;

    /// <summary>
    /// Characters cmd would act on inside quotes: <c>%</c> and, with delayed expansion, <c>!</c> and <c>^</c>. Windows
    /// forbids <c>"</c> in paths anyway.
    /// </summary>
    private static readonly char[] Unsafe = ['"', '%', '!', '^'];

    private readonly IRunnerLauncher _launcher;
    private readonly PendingUpdateFile _pending;
    private readonly string _system;
    private readonly string _msiexec;
    private readonly string _gui;
    private readonly string _cli;

    public MsiInstaller(IRunnerLauncher launcher, PendingUpdateFile pending)
        : this(launcher, pending, msiexec: null)
    {
    }

    /// <summary>Tests replace msiexec and the old version's programs with batch files.</summary>
    /// <param name="system">
    /// Where cmd, tasklist, find and ping come from, named in full: a PATH with Git's Unix tools first would run another
    /// <c>find</c>.
    /// </param>
    internal MsiInstaller(IRunnerLauncher launcher, PendingUpdateFile pending, string? msiexec, string? system = null,
        string guiFileName = "dictum-gui.exe", string cliFileName = "dictum.exe")
    {
        _launcher = launcher;
        _pending = pending;
        _system = system ?? Environment.SystemDirectory;
        _msiexec = msiexec ?? Path.Combine(_system, "msiexec.exe");
        _gui = guiFileName;
        _cli = cliFileName;
    }

    public InstallKind Kind => InstallKind.Msi;

    public void Start(DownloadedUpdate update, int waitForProcess, RestartTarget restart)
    {
        ArgumentNullException.ThrowIfNull(update);
        var target = Runner.Target(update, InstallKind.Msi);
        var log = update.InstallLog;
        var exitCode = Path.Combine(update.Folder, PendingUpdateFile.ExitCodeFile);
        var (program, argument) = restart switch
        {
            RestartTarget.DesktopApp => (Path.Combine(target, _gui), null),
            RestartTarget.Daemon => (Path.Combine(target, _cli), "daemon"),
            _ => ((string?)null, (string?)null),
        };
        string[] paths = [update.PackageFile, log, exitCode, target, _msiexec];
        if (paths.FirstOrDefault(p => p.IndexOfAny(Unsafe) >= 0) is { } bad)
        {
            throw new UpdateException(UpdateFailure.CannotInstall, $"cmd.exe cannot run the update from a path with {string.Join(" ", Unsafe)}: {bad}");
        }

        // The wait loop names the system's programs without quotes, so that its own quotes stay as they are.
        if (_system.IndexOfAny([.. Unsafe, ' ', '&', '|', '(', ')', '<', '>']) >= 0)
        {
            throw new UpdateException(UpdateFailure.CannotInstall, $"cmd.exe cannot run the update from this system folder: {_system}");
        }

        var script = Script(waitForProcess, _system, _msiexec, update.PackageFile, log, exitCode, program, argument);
        Runner.Start(_launcher, _pending, update, new ProcessStartInfo(Path.Combine(_system, "cmd.exe"))
        {
            Arguments = $"/d /v:on /s /c \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>
    /// The runner's commands, joined with <c>&amp;</c> for one <c>cmd /v:on /s /c</c>. An inner cmd waits for the process:
    /// its <c>exit 0</c> ends only the loop. <c>ping</c> sleeps a second, since <c>timeout</c> needs a console for input.
    /// <c>cmd /c</c> waits for msiexec, a GUI program, and sets <c>errorlevel</c> to its exit code.
    /// </summary>
    internal static string Script(
        int waitForProcess, string system, string msiexec, string msi, string log, string exitCode, string? restart, string? argument)
    {
        var pid = waitForProcess.ToString(CultureInfo.InvariantCulture);
        var commands = new List<string>
        {
            $"{system}\\cmd.exe /d /c \"for /l %i in (1,1,{MaxWaitSeconds}) do @({system}\\tasklist.exe /fi \"PID eq {pid}\" /nh"
            + $" | {system}\\find.exe \" {pid} \" > nul || exit 0) & {system}\\PING.EXE -n 2 127.0.0.1 > nul\"",
            $"\"{msiexec}\" /i \"{msi}\" /passive /norestart /l*v \"{log}\"",
            "set \"rc=!errorlevel!\"",
            $"> \"{exitCode}\" echo !rc!",
        };
        if (restart is not null)
        {
            commands.Add($"if !rc! neq 0 if !rc! neq {PendingUpdateFile.RestartRequired} start \"\" /b \"{restart}\"{(argument is null ? "" : " " + argument)}");
        }

        return string.Join(" & ", commands);
    }
}
