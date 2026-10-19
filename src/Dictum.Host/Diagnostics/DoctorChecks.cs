using Dictum.Audio.PortAudio;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Host.Daemon;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Setup;

namespace Dictum.Host.Diagnostics;

/// <param name="ConfigError">Set when the config file exists but cannot be loaded; <paramref name="Config"/> is then null.</param>
/// <param name="Daemon">The <c>daemon</c> row: the CLI asks the socket, the daemon reports on itself.</param>
public sealed record DoctorInputs(
    string ConfigPath,
    DictumConfig? Config,
    ConfigException? ConfigError,
    IReadOnlyList<ConfigIssue> ConfigWarnings,
    AppPaths Paths,
    PlatformServices Platform,
    HardwareInfo Hardware,
    Func<IAudioSource> Audio,
    Func<string, ISpeechToText> GetStt,
    Func<string, ITextProcessor> GetProcessor,
    Func<CancellationToken, Task<HealthStatus>> Daemon,
    TimeSpan? CheckTimeout = null)
{
    /// <summary>The CLI's checks: providers from an engine built on first use, and a daemon row that asks the socket.</summary>
    public static DoctorInputs ForCli(
        string configPath,
        DictumConfig? config,
        ConfigException? configError,
        IReadOnlyList<ConfigIssue> configWarnings,
        AppPaths paths,
        PlatformServices platform,
        Func<IDictationEngine> engine,
        string socketPath) =>
        new(
            configPath,
            config,
            configError,
            configWarnings,
            paths,
            platform,
            HardwareProbe.Detect(paths),
            () => engine().Audio,
            id => engine().GetStt(id),
            id => engine().GetProcessor(id),
            c => DoctorChecks.CheckDaemonAsync(socketPath, c));

    /// <summary>The daemon's own checks, on its live engine: the Whisper model is already loaded, so the stt rows are cheap.</summary>
    public static DoctorInputs ForEngine(
        string configPath,
        Func<IDictationEngine> engine,
        IReadOnlyList<ConfigIssue> configWarnings,
        AppPaths paths,
        PlatformServices platform) =>
        new(
            configPath,
            engine().Config,
            null,
            configWarnings,
            paths,
            platform,
            HardwareProbe.Detect(paths),
            () => engine().Audio,
            id => engine().GetStt(id),
            id => engine().GetProcessor(id),
            _ => Task.FromResult(HealthStatus.Ok($"running (pid {Environment.ProcessId}), answering this check")));
}

/// <summary>The checks behind <c>dictum doctor</c> and the desktop app's System page.</summary>
public static class DoctorChecks
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The CLI's <c>daemon</c> row: what the daemon on <paramref name="socketPath"/> says about itself.</summary>
    /// <param name="timeout">How long the daemon has to answer; the client's default when null.</param>
    public static async Task<HealthStatus> CheckDaemonAsync(string socketPath, CancellationToken ct, TimeSpan? timeout = null)
    {
        var wait = timeout ?? IpcClient.DefaultTimeout;
        var probe = await new IpcClient(socketPath).ProbeAsync(ct, wait).ConfigureAwait(false);
        if (probe.Status is { } status)
        {
            var registered = status.Profiles.Count(p => p.Registered);
            var detail = $"running (pid {status.ProcessId}), {status.State}; {registered} hotkey(s) registered via {status.HotkeyBackend}";
            return status.Warning is null ? HealthStatus.Ok(detail) : HealthStatus.Warning(detail, status.Warning);
        }

        return probe.Presence == DaemonPresence.NotAnswering
            ? HealthStatus.Error(
                $"not answering: it took the connection but sent no status within {Duration(wait)}",
                "Stop the dictum daemon process, then start it again with 'dictum daemon'.")
            : HealthStatus.Warning("not running", "Start it with 'dictum daemon', or at login with 'dictum daemon --install-autostart'.");
    }

    public static async Task<IReadOnlyList<PlatformCheck>> RunAsync(DoctorInputs inputs, CancellationToken ct)
    {
        var checks = new List<PlatformCheck>();
        void Add(string name, HealthStatus status) => checks.Add(new PlatformCheck(name, status));
        var timeout = inputs.CheckTimeout ?? DefaultTimeout;
        var config = inputs.Config;

        Add(PlatformCheckNames.Config, inputs.ConfigError is { } error
            ? HealthStatus.Error($"{error.Message} {string.Join("; ", error.Issues)}", "Fix the file, then run 'dictum config validate'.")
            : config is null
                ? HealthStatus.Warning($"No config at {inputs.ConfigPath}", "Run 'dictum init'.")
                : HealthStatus.Ok($"{inputs.ConfigPath} ({config.Stt.Count} STT, {config.Processors.Count} processors, {config.Profiles.Count} profiles)"));
        if (inputs.ConfigWarnings.Count > 0)
        {
            Add(PlatformCheckNames.ConfigWarnings, HealthStatus.Warning(
                $"{inputs.ConfigWarnings.Count} warning(s)",
                string.Join("; ", inputs.ConfigWarnings)));
        }

        Add(PlatformCheckNames.Models, CheckModels(config, inputs.Paths));
        Add(PlatformCheckNames.Gpu, HardwareText.Gpu(inputs.Hardware) is { } gpu
            ? HealthStatus.Ok(gpu)
            : HealthStatus.Ok("none that Whisper can use; speech recognition runs on the CPU"));
        CheckAudio(inputs.Audio, Add);

        foreach (var check in await inputs.Platform.DiagnoseAsync(ct).ConfigureAwait(false))
        {
            Add(check.Name, check.Status);
        }

        Add(PlatformCheckNames.Daemon, await CheckAsync(() => inputs.Daemon(ct), timeout, ct).ConfigureAwait(false));

        if (config is not null)
        {
            foreach (var id in config.Stt.Keys)
            {
                Add(PlatformCheckNames.Stt(id), await CheckAsync(() => inputs.GetStt(id).CheckAsync(ct), timeout, ct).ConfigureAwait(false));
            }

            foreach (var id in config.Processors.Keys)
            {
                if (inputs.GetProcessor(id) is IHealthCheck check)
                {
                    Add(PlatformCheckNames.Processor(id), await CheckAsync(() => check.CheckAsync(ct), timeout, ct).ConfigureAwait(false));
                }
            }
        }

        return checks;
    }

    private static HealthStatus CheckModels(DictumConfig? config, AppPaths paths)
    {
        var needed = (config is null ? [] : config.Stt.Values.OfType<WhisperLocalSttConfig>())
            .SelectMany(w => w.Vad ? new[] { w.Model, w.VadModel } : [w.Model])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missing = needed.Where(m => !File.Exists(paths.ResolveModel(m))).ToList();
        if (missing.Count == 0)
        {
            return HealthStatus.Ok($"{paths.ModelsDirectory} ({needed.Count} configured file(s) present)");
        }

        var pulls = missing.Select(m => Catalog.BuiltIn.ForFile(m)?.Id).OfType<string>().ToList();
        return HealthStatus.Error(
            $"Missing in {paths.ModelsDirectory}: {string.Join(", ", missing)}",
            pulls.Count == missing.Count ? $"Run 'dictum models pull {string.Join(' ', pulls)}'." : "Run 'dictum init', or copy the files there.");
    }

    private static void CheckAudio(Func<IAudioSource> audio, Action<string, HealthStatus> add)
    {
        if (Environment.GetEnvironmentVariable(DictumEnvironment.AudioFile) is { Length: > 0 } file)
        {
            add(PlatformCheckNames.Microphone, HealthStatus.Warning(
                $"Replaced by {DictumEnvironment.AudioFile}={file}",
                $"Unset {DictumEnvironment.AudioFile} to record from the microphone."));
            return;
        }

        try
        {
            var devices = audio().ListDevices();
            add(PlatformCheckNames.AudioLibrary, HealthStatus.Ok($"{PortAudioLibrary.Version} ({PortAudioLibrary.LoadedFrom})"));
            var chosen = devices.FirstOrDefault(d => d.IsDefault) ?? (devices.Count > 0 ? devices[0] : null);
            add(PlatformCheckNames.Microphone, chosen is null
                ? HealthStatus.Warning("No microphone found", "Connect one, or check that the OS allows microphone access.")
                : HealthStatus.Ok($"{chosen.Name} ({chosen.HostApi}, {chosen.DefaultSampleRate:F0} Hz); {devices.Count} input device(s)"));
        }
        catch (AudioDeviceException ex)
        {
            add(PlatformCheckNames.AudioLibrary, HealthStatus.Error(ex.Message, ex.Hint));
        }
    }

    private static async Task<HealthStatus> CheckAsync(Func<Task<HealthStatus>> check, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var task = check();
            var finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            return finished == task
                ? await task.ConfigureAwait(false)
                : HealthStatus.Error($"No answer within {Duration(timeout)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthStatus.Error(ex.Message);
        }
    }

    private static string Duration(TimeSpan time) =>
        time.TotalSeconds >= 1 ? $"{time.TotalSeconds:F0} s" : $"{time.TotalMilliseconds:F0} ms";
}
