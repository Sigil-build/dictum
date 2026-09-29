using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dictum.Audio.PortAudio;
using Dictum.Cli.Output;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Cli.Commands;

internal static class DoctorCommand
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("doctor", "Check config, microphone and every provider, with hints for anything broken") { json };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            var checks = new List<CheckJson>();
            void Add(string name, HealthStatus status) => checks.Add(new CheckJson(name, status.Level, status.Detail, status.Hint));

            DictumConfig? config = null;
            try
            {
                config = context.TryLoadConfig();
                Add("config", config is null
                    ? HealthStatus.Warning($"No config at {context.ConfigPath}", "Run 'dictum config init'.")
                    : HealthStatus.Ok($"{context.ConfigPath} ({config.Stt.Count} STT, {config.Processors.Count} processors, {config.Profiles.Count} profiles)"));
            }
            catch (ConfigException ex)
            {
                Add("config", HealthStatus.Error(
                    $"{ex.Message} {string.Join("; ", ex.Issues)}",
                    "Fix the file, then run 'dictum config validate'."));
            }

            Add("models", HealthStatus.Ok(context.Paths.ModelsDirectory));
            CheckAudio(context, Add);

            if (config is not null)
            {
                var host = context.Host();
                foreach (var id in config.Stt.Keys)
                {
                    Add($"stt:{id}", await CheckAsync(() => host.GetStt(id).CheckAsync(token), token).ConfigureAwait(false));
                }

                foreach (var id in config.Processors.Keys)
                {
                    if (host.GetProcessor(id) is IHealthCheck check)
                    {
                        Add($"processor:{id}", await CheckAsync(() => check.CheckAsync(token), token).ConfigureAwait(false));
                    }
                }
            }

            if (parse.GetValue(json))
            {
                var report = new DoctorJson(CliApp.Version, RuntimeInformation.OSDescription, RuntimeInformation.RuntimeIdentifier, checks);
                context.Out.WriteLine(JsonSerializer.Serialize(report, CliJsonContext.Readable.DoctorJson));
            }
            else
            {
                context.Out.WriteLine($"dictum {CliApp.Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})");
                foreach (var check in checks)
                {
                    context.Out.WriteLine($"{Label(check.Level)} {check.Name,-22} {check.Detail}");
                    if (check.Hint is not null && check.Level != HealthLevel.Ok)
                    {
                        context.Out.WriteLine($"     {"",-22} → {check.Hint}");
                    }
                }
            }

            return checks.Any(c => c.Level == HealthLevel.Error) ? ExitCodes.Error : ExitCodes.Success;
        }));

        return command;
    }

    private static void CheckAudio(CliContext context, Action<string, HealthStatus> add)
    {
        try
        {
            var devices = context.Host().Audio.ListDevices();
            add("audio library", HealthStatus.Ok($"{PortAudioLibrary.Version} ({PortAudioLibrary.LoadedFrom})"));
            var chosen = devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
            add("microphone", chosen is null
                ? HealthStatus.Warning("No microphone found", "Connect one, or check that the OS allows microphone access.")
                : HealthStatus.Ok($"{chosen.Name} ({chosen.HostApi}, {chosen.DefaultSampleRate:F0} Hz); {devices.Count} input device(s)"));
        }
        catch (AudioDeviceException ex)
        {
            add("audio library", HealthStatus.Error(ex.Message, ex.Hint));
        }
    }

    private static async Task<HealthStatus> CheckAsync(Func<Task<HealthStatus>> check, CancellationToken ct)
    {
        try
        {
            var task = check();
            var finished = await Task.WhenAny(task, Task.Delay(CheckTimeout, ct)).ConfigureAwait(false);
            return finished == task
                ? await task.ConfigureAwait(false)
                : HealthStatus.Error($"No answer within {CheckTimeout.TotalSeconds:F0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthStatus.Error(ex.Message);
        }
    }

    private static string Label(HealthLevel level) => level switch
    {
        HealthLevel.Ok => "  OK",
        HealthLevel.Warning => "WARN",
        _ => " ERR",
    };
}
