using System.CommandLine;
using System.Runtime.InteropServices;
using Dictum.Audio.PortAudio;
using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Host.Daemon;
using Dictum.Host.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.Cli.Commands;

internal static class DaemonCommand
{
    /// <summary>How long a stop waits for subscribers to hear that the daemon stops, so they do not restart it.</summary>
    private static readonly TimeSpan StoppingAnnounceTimeout = TimeSpan.FromSeconds(1);

    public static Command Create()
    {
        var install = new Option<bool>("--install-autostart") { Description = "Start the daemon at login, then exit" };
        var uninstall = new Option<bool>("--uninstall-autostart") { Description = "Stop starting the daemon at login, then exit" };
        var command = new Command(DictumExecutables.DaemonVerb, "Run in the background: hotkeys, dictation and the control socket (Ctrl+C stops)")
        {
            install,
            uninstall,
        };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, token) =>
            parse.GetValue(install) || parse.GetValue(uninstall)
                ? ConfigureAutostartAsync(context, parse.GetValue(install), token)
                : ServeAsync(context, token), ct, consoleLevel: LogLevel.Info));

        return command;
    }

    private static async Task<int> ServeAsync(CliContext context, CancellationToken ct)
    {
        var (config, warnings) = context.Store.LoadWithIssues();
        context.UseConfig(config);
        var platform = context.Platform;
        var log = context.Log;

        var events = new IpcEventHub();
        await using var daemon = new DictumDaemon(config, context.CreateHost, platform, new SoundPlayer(log), log, events, warnings);
        await using var server = new IpcServer(context.SocketPath, log, events);
        server.Start(daemon.HandleAsync);
        await daemon.StartAsync(ct).ConfigureAwait(false);
        using var watcher = new ConfigWatcher(
            context.Store,
            async (changed, changedWarnings) =>
            {
                context.UseConfig(changed);
                await daemon.ApplyConfigAsync(changed, ct, changedWarnings).ConfigureAwait(false);
            },
            invalid => platform.Notifier.NotifyAsync("Dictum: config not applied", invalid.Message, NotificationKind.Error, ct),
            log);

        daemon.ReloadHandler = async c =>
        {
            var (fresh, freshWarnings) = context.Store.LoadWithIssues();
            context.UseConfig(fresh);
            await daemon.ApplyConfigAsync(fresh, c, freshWarnings).ConfigureAwait(false);
        };
        daemon.DoctorHandler = c => RunOwnChecksAsync(context, daemon, platform, c);

        PrintSummary(context, daemon.Status());

        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            terminated.TrySetResult();
        });
        await Task.WhenAny(Task.Delay(Timeout.Infinite, ct), terminated.Task, daemon.ShutdownRequested).ConfigureAwait(false);

        log.Info("Daemon stopping");
        await daemon.AnnounceStoppingAsync(StoppingAnnounceTimeout).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    /// <summary>The checks run on the daemon's live engine: the Whisper model is already loaded, so the stt rows are cheap.</summary>
    private static Task<IReadOnlyList<PlatformCheck>> RunOwnChecksAsync(
        CliContext context,
        DictumDaemon daemon,
        PlatformServices platform,
        CancellationToken ct) =>
        DoctorChecks.RunAsync(new DoctorInputs(
            context.ConfigPath,
            daemon.Config,
            null,
            daemon.ConfigWarnings,
            context.Paths,
            platform,
            HardwareProbe.Detect(context.Paths),
            () => daemon.Engine.Audio,
            id => daemon.Engine.GetStt(id),
            id => daemon.Engine.GetProcessor(id),
            _ => Task.FromResult(HealthStatus.Ok($"running (pid {Environment.ProcessId}), answering this check"))), ct);

    private static void PrintSummary(CliContext context, DaemonStatus status)
    {
        var e = context.Error;
        e.WriteLine($"Dictum daemon running (pid {status.ProcessId}). Ctrl+C stops it.");
        e.WriteLine($"  hotkeys: {status.HotkeyBackend}");
        e.WriteLine($"  paste:   {status.InjectorBackend}");
        foreach (var profile in status.Profiles)
        {
            var hotkey = profile.Hotkey is null ? "no hotkey" : $"{profile.Hotkey} ({profile.Mode}{(profile.Registered ? "" : ", not registered")})";
            e.WriteLine($"  {profile.Id,-16} {hotkey}");
        }

        if (status.Warning is not null)
        {
            e.WriteLine($"warning: {status.Warning}");
        }
    }

    private static async Task<int> ConfigureAutostartAsync(CliContext context, bool install, CancellationToken ct)
    {
        var autostart = context.Platform.Autostart
            ?? throw new InvalidOperationException($"Autostart is not supported on {context.Platform.Name} yet.");
        if (install)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the dictum executable path.");
            await autostart.InstallAsync(InstallLocation.Daemon(executable, InstallLocation.AppImage), ct).ConfigureAwait(false);
            context.Out.WriteLine(autostart.Location(LoginApp.Daemon));
            context.Error.WriteLine("The daemon will start at your next login.");
        }
        else
        {
            await autostart.UninstallAsync(ct).ConfigureAwait(false);
            context.Error.WriteLine("Dictum no longer starts at login.");
        }

        return ExitCodes.Success;
    }
}
