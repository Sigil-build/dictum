using System.CommandLine;
using System.Diagnostics;
using Dictum.Cli.Output;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Text;
using Dictum.Host.Ipc;
using Dictum.Host.Updates;
using Dictum.Platform;
using Dictum.Setup;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Cli.Commands;

/// <summary>
/// Checks for a newer Dictum and installs it the way this copy was installed. It downloads and verifies in the foreground,
/// so its exit code reports those, then hands the install to a runner that waits for this process to exit: a
/// <c>dictum</c> running from the install folder would keep the installer from replacing it.
/// </summary>
internal static class UpdateCommand
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The update service; a test gives its own.</summary>
    internal static Func<CliContext, UpdateService>? Service { get; set; }

    /// <summary>What runs now, and so starts again after the install; a test says which.</summary>
    internal static Func<CliContext, CancellationToken, Task<RestartTarget>>? Running { get; set; }

    public static Command Create()
    {
        var check = new Option<bool>("--check") { Description = "Only check: exit code 0 when up to date, 6 when an update is available" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask before downloading and installing" };
        var command = new Command(
            "update", "Download, verify and install the newest Dictum, the way this copy was installed. Exit code 5: the download failed; 7: it did not verify") { check, yes };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse,
            (context, token) => RunAsync(context, parse.GetValue(check), parse.GetValue(yes), token), ct));
        return command;
    }

    private static async Task<int> RunAsync(CliContext context, bool checkOnly, bool yes, CancellationToken ct)
    {
        var service = Service?.Invoke(context) ?? UpdatesFactory.Create(context.Paths, CliApp.Version, context.Log);
        if (service.Current is not { } current)
        {
            context.Out.WriteLine($"This build of Dictum ({CliApp.Version}) has no release version, so there is nothing to compare.");
            return ExitCodes.Success;
        }

        if (ReportLast(context, current) is { } installing)
        {
            var text = $"The update to {installing.Version} is still being installed. Log: {installing.Log}";
            if (!checkOnly)
            {
                throw new UpdateException(UpdateFailure.CannotInstall, text);
            }

            context.Out.WriteLine(TerminalText.Show(text));
        }

        var (offer, lookup) = await service.FindAsync(ct).ConfigureAwait(false);
        if (lookup.Release is null)
        {
            context.Error.WriteLine($"error: Could not check for updates: {TerminalText.Show(lookup.Detail ?? lookup.Failure.ToString())}");
            return ExitCodes.Error;
        }

        if (offer is null)
        {
            context.Out.WriteLine($"Dictum {current} is up to date.");
            return ExitCodes.Success;
        }

        context.Out.WriteLine($"Dictum {current} → {offer.Version} is available. {offer.Page.AbsoluteUri}");
        if (checkOnly)
        {
            return ExitCodes.UpdateAvailable;
        }

        switch (offer.Installation.Kind)
        {
            case InstallKind.Portable:
                context.Out.WriteLine("This copy of Dictum was not installed, so it does not update itself. Download the new version from the page above.");
                return ExitCodes.Success;
            case InstallKind.PackageManager:
                context.Out.WriteLine("Dictum was installed by your package manager. Build the new package:");
                foreach (var line in ManualUpdate.PackageManagerCommands(offer.Version).Split('\n'))
                {
                    context.Out.WriteLine($"  {line}");
                }

                return ExitCodes.Success;
        }

        var package = offer.Package
            ?? throw new UpdateException(UpdateFailure.NoPackage, "This release has no package for this installation. Download it from the page above.");
        var restart = await (Running ?? FindRunningAsync)(context, ct).ConfigureAwait(false);
        Describe(context, offer, package, restart);
        if (!yes && !Confirm(context))
        {
            context.Out.WriteLine("Nothing changed.");
            return ExitCodes.Success;
        }

        DownloadedUpdate update;
        using (var progress = new Progress(context.Error, !Console.IsErrorRedirected, package))
        {
            update = await service.DownloadAsync(offer, progress, ct).ConfigureAwait(false);
        }

        service.Install(update, restart);
        if (offer.Installation.Kind == InstallKind.AppImage && restart == RestartTarget.Daemon)
        {
            // The runner's --quit runs from the new AppImage's mount and would not reach a daemon from the old one.
            await StopDaemonAsync(context, ct).ConfigureAwait(false);
        }

        context.Out.WriteLine(TerminalText.Show(restart == RestartTarget.None
            ? $"Installing {offer.Version} now. Log: {update.InstallLog}"
            : $"Installing {offer.Version} now; Dictum starts again when it is done. Log: {update.InstallLog}"));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Says how the update that the last run started ended, once, and deletes the update files it no longer needs. Leaves
    /// the record of one that may still be installing, and returns it.
    /// </summary>
    private static PendingUpdate? ReportLast(CliContext context, ReleaseVersion current)
    {
        var pending = new PendingUpdateFile(context.Paths.UpdatesDirectory, context.Log);
        if (pending.Installing(current, DateTimeOffset.Now) is { } installing)
        {
            return installing;
        }

        var report = pending.Take(current);
        UpdatesFolder.Clean(context.Paths.UpdatesDirectory, current, context.Log);
        var line = report.Outcome switch
        {
            UpdateOutcome.Updated => $"Dictum was updated to {report.Version}.",
            UpdateOutcome.DidNotFinish => $"The update to {report.Version} did not finish. Log: {report.Log}",
            UpdateOutcome.NeedsRestart => $"Restart Windows to finish the update to {report.Version}.",
            _ => null,
        };
        if (line is not null)
        {
            context.Out.WriteLine(TerminalText.Show(line));
        }

        return null;
    }

    private static void Describe(CliContext context, UpdateOffer offer, ReleaseAsset package, RestartTarget restart)
    {
        var target = offer.Installation.Target;
        var (kind, action) = offer.Installation.Kind switch
        {
            InstallKind.Msi => ("MSI", "install it with Windows Installer"),
            InstallKind.AppImage => ("AppImage", $"replace {target}"),
            _ => ("install.sh", $"install it into {target} with its install.sh"),
        };
        context.Out.WriteLine(TerminalText.Show($"Installed as: {kind} ({target})"));
        var download = $"Dictum will download {package.Name} ({SetupRunner.Format(package.Size)})";
        context.Out.WriteLine(TerminalText.Show(restart == RestartTarget.None
            ? $"{download}, verify it and {action}."
            : $"{download}, verify it, {action} and restart Dictum."));
    }

    private static bool Confirm(CliContext context)
    {
        if (Console.IsInputRedirected)
        {
            throw new ConfigException("Input is not a terminal. Run 'dictum update --yes' to update without asking.");
        }

        return new ConsolePrompt(context.Error).Confirm("Continue?", defaultValue: false);
    }

    /// <summary>The desktop app if it runs, which starts its own daemon; else a daemon that holds the socket; else nothing.</summary>
    private static async Task<RestartTarget> FindRunningAsync(CliContext context, CancellationToken ct)
    {
        var apps = Process.GetProcessesByName(DictumExecutables.DesktopApp);
        foreach (var app in apps)
        {
            app.Dispose();
        }

        if (apps.Length > 0)
        {
            return RestartTarget.DesktopApp;
        }

        var probe = await new IpcClient(context.SocketPath).ProbeAsync(ct, ProbeTimeout).ConfigureAwait(false);
        return probe.Presence == DaemonPresence.NotRunning ? RestartTarget.None : RestartTarget.Daemon;
    }

    private static async Task StopDaemonAsync(CliContext context, CancellationToken ct)
    {
        try
        {
            await new IpcClient(context.SocketPath).SendAsync(new IpcRequest(IpcCommand.Shutdown), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DaemonNotRunningException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            context.Log.Warn($"The daemon was not stopped before the update: {ex.Message}");
        }
    }

    /// <summary>One line per stage on stderr, and the download's bar when stderr is a terminal.</summary>
    private sealed class Progress(TextWriter error, bool redraw, ReleaseAsset package) : IProgress<UpdateProgress>, IDisposable
    {
        private readonly SetupProgressView _bar = new(error, redraw);
        private UpdateStage? _stage;

        public void Report(UpdateProgress value)
        {
            if (value.Stage != _stage)
            {
                _bar.Dispose();
                _stage = value.Stage;
                error.WriteLine(value.Stage == UpdateStage.Downloading
                    ? $"Downloading {package.Name} ({SetupRunner.Format(package.Size)})"
                    : $"Verifying {package.Name}");
            }

            if (value.Stage == UpdateStage.Downloading)
            {
                _bar.Bar(value.Bytes, value.Total);
            }
        }

        public void Dispose() => _bar.Dispose();
    }
}
