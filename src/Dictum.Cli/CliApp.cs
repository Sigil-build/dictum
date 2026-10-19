using System.CommandLine;
using System.Reflection;
using Dictum.Audio;
using Dictum.Audio.PortAudio;
using Dictum.Cli.Commands;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Injection;
using Dictum.Setup;

namespace Dictum.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int InvalidConfig = 2;
    public const int ProviderUnavailable = 3;
    public const int DaemonNotRunning = 4;
    public const int DownloadFailed = 5;
    public const int Interrupted = 130;
}

internal static class CliApp
{
    public static readonly Option<string?> ConfigOption = new("--config")
    {
        Description = "Config file to use instead of the default location",
        Recursive = true,
    };

    public static readonly Option<bool> VerboseOption = new("--verbose", "-v")
    {
        Description = "Log details to stderr, including dictated text",
        Recursive = true,
    };

    public static string Version { get; } =
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string UserAgent => $"dictum/{Version}";

    public static RootCommand BuildRoot()
    {
        var root = new RootCommand("Dictum: voice to text. Results go to stdout; logs and progress go to stderr.")
        {
            ConfigOption,
            VerboseOption,
        };

        root.Add(InitCommand.Create());
        root.Add(TranscribeCommand.Create());
        root.Add(RecordCommand.Create());
        root.Add(ProcessCommand.Create());
        root.Add(SttCommand.Create());
        root.Add(ProfilesCommand.Create());
        root.Add(VocabCommand.Create());
        root.Add(ModelsCommand.Create());
        root.Add(DevicesCommand.Create());
        root.Add(ConfigCommand.Create());
        root.Add(HistoryCommand.Create());
        root.Add(DoctorCommand.Create());
        root.Add(DaemonCommand.Create());
        foreach (var control in ControlCommands.Create())
        {
            root.Add(control);
        }

        root.Add(SecretCommand.Create());
        return root;
    }

    /// <summary>Runs a command body with a <see cref="CliContext"/> and maps exceptions to exit codes.</summary>
    public static async Task<int> RunAsync(
        ParseResult parse,
        Func<CliContext, CancellationToken, Task<int>> body,
        CancellationToken ct,
        Core.Diagnostics.LogLevel? consoleLevel = null)
    {
        await using var context = new CliContext(parse, consoleLevel);
        try
        {
            return await body(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            context.Error.WriteLine("Interrupted.");
            return ExitCodes.Interrupted;
        }
        catch (Exception ex)
        {
            return Report(context, ex);
        }
    }

    private static int WithHint(CliContext context, string message, string? hint)
    {
        context.Error.WriteLine($"error: {message}");
        if (hint is not null)
        {
            context.Error.WriteLine($"  hint: {hint}");
        }

        return ExitCodes.Error;
    }

    private static int Report(CliContext context, Exception ex)
    {
        var inner = ex is DictationException { InnerException: { } cause } ? cause : ex;
        context.Log.Write(Core.Diagnostics.LogLevel.Debug, ex.ToString());
        switch (inner)
        {
            case ConfigException config:
                context.Error.WriteLine($"error: {config.Message}");
                foreach (var issue in config.Issues)
                {
                    context.Error.WriteLine($"  {issue}");
                }

                return ExitCodes.InvalidConfig;
            case ProviderUnavailableException or HttpRequestException:
                context.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.ProviderUnavailable;
            case DaemonNotRunningException:
                context.Error.WriteLine($"error: {inner.Message}");
                return ExitCodes.DaemonNotRunning;
            case AudioDeviceException audio:
                return WithHint(context, audio.Message, audio.Hint);
            case HotkeyUnavailableException hotkey:
                return WithHint(context, hotkey.Message, hotkey.Hint);
            case InjectionUnavailableException injection:
                return WithHint(context, injection.Message, injection.Hint);
            case DownloadException download:
                context.Error.WriteLine($"error: {download.Message}");
                return ExitCodes.DownloadFailed;
            case DaemonAlreadyRunningException:
            case WavFormatException or FileNotFoundException or DirectoryNotFoundException:
                context.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Error;
            default:
                context.Error.WriteLine($"error: {ex.Message}");
                if (!context.Verbose)
                {
                    context.Error.WriteLine("  (run with --verbose for details)");
                }
                else
                {
                    context.Error.WriteLine(ex);
                }

                return ExitCodes.Error;
        }
    }
}
