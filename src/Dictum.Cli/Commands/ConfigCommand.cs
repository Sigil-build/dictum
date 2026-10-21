using System.CommandLine;
using System.Diagnostics;
using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.Cli.Commands;

internal static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", "Create, locate, validate and edit the config file");

        var path = new Command("path", "Print the config file path");
        path.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            context.Out.WriteLine(context.ConfigPath);
            return Task.FromResult(ExitCodes.Success);
        }, ct));

        var force = new Option<bool>("--force") { Description = "Overwrite an existing config" };
        var init = new Command("init", "Write a starter config without downloading anything (see 'dictum init')") { force };
        init.Hidden = true;
        init.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            if (context.Store.Exists && !parse.GetValue(force))
            {
                throw new ConfigException($"Config already exists: {context.ConfigPath}. Use --force to overwrite it.");
            }

            context.Store.Save(DefaultConfig.Create());
            Directory.CreateDirectory(context.Paths.ModelsDirectory);
            context.Out.WriteLine(context.ConfigPath);
            context.Error.WriteLine($"Created a starter config. Download its speech model with 'dictum models pull {SetupPlanner.CpuModel}',");
            context.Error.WriteLine("or run 'dictum init' instead, which also chooses a model for this machine.");
            return Task.FromResult(ExitCodes.Success);
        }, ct));

        var validate = new Command("validate", "Check the config for errors");
        validate.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var (config, warnings) = context.Store.LoadWithIssues();
            foreach (var warning in warnings)
            {
                context.Error.WriteLine($"warning: {warning}");
            }

            context.Out.WriteLine($"OK: {config.Stt.Count} STT providers, {config.Processors.Count} processors, {config.Profiles.Count} profiles");
            return Task.FromResult(ExitCodes.Success);
        }, ct));

        var edit = new Command("edit", "Open the config in $VISUAL / $EDITOR (or the system editor)");
        edit.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            if (!context.Store.Exists)
            {
                throw new ConfigException($"No config at {context.ConfigPath}. Run 'dictum init' first.");
            }

            var editor = Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } visual ? visual
                : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } env ? env
                : null;
            var start = editor is not null
                ? new ProcessStartInfo(editor) { ArgumentList = { context.ConfigPath }, UseShellExecute = false }
                : OperatingSystem.IsWindows() ? new ProcessStartInfo("notepad.exe") { ArgumentList = { context.ConfigPath } }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { "-W", "-t", context.ConfigPath } }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { context.ConfigPath } };

            using (var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}."))
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }

            // xdg-open returns immediately, so validation may run before the editor saves.
            var (_, warnings) = context.Store.LoadWithIssues();
            foreach (var warning in warnings)
            {
                context.Error.WriteLine($"warning: {warning}");
            }

            context.Error.WriteLine("Config is valid.");
            return ExitCodes.Success;
        }, ct));

        command.Add(path);
        command.Add(init);
        command.Add(validate);
        command.Add(edit);
        return command;
    }
}
