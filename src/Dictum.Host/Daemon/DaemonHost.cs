using System.Runtime.InteropServices;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.Host.Daemon;

/// <param name="Config">The config the daemon starts with, already loaded by the caller.</param>
/// <param name="Store">The config file that the watcher and the <c>reload</c> command read again.</param>
/// <param name="UseConfig">Gets every config the daemon is about to apply, so settings read at use time follow it.</param>
/// <param name="Started">Gets the status once the daemon serves and its hotkeys are registered.</param>
/// <param name="Sounds">The cue player, which stays the caller's; the default plays through the sound device.</param>
public sealed record DaemonHostOptions(
    DictumConfig Config,
    IReadOnlyList<ConfigIssue> Warnings,
    ConfigStore Store,
    AppPaths Paths,
    string SocketPath,
    PlatformServices Platform,
    ILog Log,
    Func<DictumConfig, IDictationEngine> CreateEngine,
    Action<DictumConfig> UseConfig,
    Action<DaemonStatus> Started,
    ISoundPlayer? Sounds = null);

/// <summary>
/// The daemon process: the event hub, the daemon, its IPC server and the config watcher, from start until Ctrl+C,
/// SIGTERM or a client's <c>shutdown</c>.
/// </summary>
public static class DaemonHost
{
    /// <summary>How long a stop waits for subscribers to hear that the daemon stops, so they do not restart it.</summary>
    private static readonly TimeSpan StoppingAnnounceTimeout = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(DaemonHostOptions options, CancellationToken ct)
    {
        var (store, platform, log) = (options.Store, options.Platform, options.Log);
        var events = new IpcEventHub();
        var commands = new DaemonHostCommands(
            () =>
            {
                var loaded = store.LoadWithIssues();
                options.UseConfig(loaded.Config);
                return loaded;
            },
            (d, c) => DoctorChecks.RunAsync(DoctorInputs.ForEngine(store.Path, () => d.Engine, d.ConfigWarnings, options.Paths, platform), c));

        // Only a player made here is disposed here, after the daemon, which plays cues until its own disposal.
        var sounds = options.Sounds ?? DictumHost.CreateSoundPlayer(log);
        using var ownedSounds = options.Sounds is null ? sounds as IDisposable : null;
        await using var daemon = new DictumDaemon(
            options.Config, options.CreateEngine, platform, sounds, log, events, options.Warnings, commands);

        // The server listens before the hotkeys are registered, which can take seconds on a portal.
        await using var server = new IpcServer(options.SocketPath, log, events);
        server.Start(daemon.HandleAsync);
        await daemon.StartAsync(ct).ConfigureAwait(false);
        using var watcher = new ConfigWatcher(
            store,
            async (changed, changedWarnings) =>
            {
                options.UseConfig(changed);
                await daemon.ApplyConfigAsync(changed, ct, changedWarnings).ConfigureAwait(false);
            },
            invalid => platform.Notifier.NotifyAsync("Dictum: config not applied", invalid.Message, NotificationKind.Error, ct),
            log);

        options.Started(daemon.Status());

        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            terminated.TrySetResult();
        });
        await Task.WhenAny(Task.Delay(Timeout.Infinite, ct), terminated.Task, daemon.ShutdownRequested).ConfigureAwait(false);

        log.Info("Daemon stopping");

        // Before the server's disposal: a client that sees the socket close unannounced restarts the daemon.
        await daemon.AnnounceStoppingAsync(StoppingAnnounceTimeout).ConfigureAwait(false);
    }
}
