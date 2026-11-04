using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Docs;
using Dictum.Plugins;

namespace Dictum.Cli.Commands;

/// <summary>
/// Lists the speech engines that run as plugins, adds one the user trusts (a program Dictum did not check) and removes
/// one. Adding runs the program once without a model, so a plugin that cannot start is never recorded.
/// </summary>
internal static class PluginsCommand
{
    /// <summary>Verbatim in the GUI too.</summary>
    internal const string TrustText = "This plugin is a program that runs on your computer. Dictum did not check it. Add it only if you trust where it came from.";

    private const string BuiltInState = "built in";

    public static Command Create()
    {
        var command = new Command("plugins", "List, add and remove speech engines that run as plugins");
        command.Add(List());
        command.Add(Add());
        command.Add(Remove());
        return command;
    }

    private static Command List()
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var list = new Command("list", "Show Whisper and the plugins you added, and whether each is confirmed") { json };
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var rows = Rows(context);
            if (parse.GetValue(json))
            {
                context.Out.WriteLine(JsonSerializer.Serialize(rows, CliJsonContext.Readable.ListPluginJson));
                return Task.FromResult(ExitCodes.Success);
            }

            foreach (var row in rows)
            {
                var name = row.Version is null ? row.Name : $"{row.Name} {row.Version}";
                context.Out.WriteLine(row.Path is null
                    ? $"{row.Id}  {name}  {row.State}"
                    : $"{row.Id}  {name}  your own · {row.Path} · {row.State}");
            }

            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return list;
    }

    private static Command Add()
    {
        var folderArgument = new Argument<string>("folder") { Description = "The plugin's folder, with its plugin.json" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask whether to add it" };
        var add = new Command("add", "Add a plugin from a folder, or confirm it again after it changed") { folderArgument, yes };
        add.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var folder = Normalize(parse.GetValue(folderArgument)!);
            var config = context.TryLoadConfig() ?? throw new ConfigException("No config yet; run 'dictum init' first.");
            var manifest = LoadManifest(folder);
            var registered = config.Plugins.TryGetValue(manifest.Id, out var existing);
            if (registered && !string.Equals(Normalize(existing!.Path), folder, PathComparison.Current))
            {
                throw new ConfigException(
                    $"A plugin named {manifest.Id} is already added from {existing.Path}. Remove it first: dictum plugins remove {manifest.Id}");
            }

            // Hashed now, before the user is asked and before the program runs, so the record pins the files as they
            // are shown, not as the plugin leaves them; and a trust file this Dictum may not change refuses at once.
            var executable = manifest.ExecutablePath(folder);
            var trust = new PluginTrustStore(context.Paths.PluginTrustFile, context.Log);
            var confirmation = trust.Prepare(manifest.Id, folder, executable);

            context.Error.WriteLine($"{manifest.Name} {manifest.Version} by {manifest.Publisher ?? "an unknown publisher"} from {folder}");
            if (registered)
            {
                context.Error.WriteLine($"{manifest.Id} is already added; confirming it again.");
            }

            // A change to a script that a launcher runs would go unnoticed, so the user hears it before saying yes.
            if (manifest.Program is not { Count: > 0 })
            {
                context.Error.WriteLine(
                    $"plugin.json lists no program files, so Dictum checks only plugin.json and {manifest.Executable[PluginProtocol.CurrentRid]} for changes.");
            }

            context.Error.WriteLine(TrustText);
            if (!parse.GetValue(yes))
            {
                if (Console.IsInputRedirected)
                {
                    throw new ConfigException("Input is not a terminal. Run 'dictum plugins add <folder> --yes' to add it without asking.");
                }

                if (!new ConsolePrompt(context.Error).Confirm("Add it?", defaultValue: false))
                {
                    context.Error.WriteLine("Nothing was changed.");
                    return ExitCodes.Success;
                }
            }

            await Probe(context, new PluginLaunch(manifest.Id, manifest.Name, manifest.Version, folder, executable, Model: null, Threads: null), token).ConfigureAwait(false);

            trust.Confirm(confirmation);

            // A re-confirm changes nothing in the config, so it is not rewritten: a running daemon would reload it for nothing.
            if (!registered)
            {
                config.Plugins[manifest.Id] = new PluginRegistration { Path = folder };
                context.SaveValidated(config);
            }

            context.Out.WriteLine($"Added {manifest.Name} {manifest.Version} (protocol {PluginProtocol.Version}).");

            // The next step, a speech provider that uses the plugin, is in the guide: in the UI language, like doctor's links.
            var language = DocsLinks.Language(config.General.UiLanguage, CultureInfo.CurrentUICulture);
            context.Out.WriteLine($"see: {DocsLinks.For(DocsPage.WritePlugin, language, DocsAnchors.WritePlugin.Use).AbsoluteUri}");
            return ExitCodes.Success;
        }, ct));
        return add;
    }

    private static Command Remove()
    {
        var id = new Argument<string>("id") { Description = "The plugin's id, as 'dictum plugins list' shows it" };
        var remove = new Command("remove", "Forget a plugin; its folder is left alone") { id };
        remove.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var pluginId = parse.GetValue(id)!;
            var config = context.LoadConfig();
            if (!config.Plugins.TryGetValue(pluginId, out var registration))
            {
                throw new ConfigException($"{pluginId} is not added.");
            }

            var users = config.Stt.Where(s => s.Value is PluginSttConfig p && p.Plugin == pluginId).Select(s => s.Key).ToList();
            if (users.Count > 0)
            {
                throw new ConfigException($"{pluginId} is used by the speech provider(s) {string.Join(", ", users)}; change or remove them first.");
            }

            config.Plugins.Remove(pluginId);
            context.SaveValidated(config);
            new PluginTrustStore(context.Paths.PluginTrustFile, context.Log).Forget(pluginId);
            context.Out.WriteLine($"Removed {pluginId}. Its folder {registration.Path} was left as it was.");
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return remove;
    }

    /// <summary>Starts the program with no model, so that it answers who it is, then stops it.</summary>
    private static async Task Probe(CliContext context, PluginLaunch launch, CancellationToken ct)
    {
        try
        {
            await using var process = await PluginProcess.StartAsync(launch, context.Plugins.Http, context.Log, PluginTimings.Default, ct).ConfigureAwait(false);
            await process.StopAsync().ConfigureAwait(false);
        }
        catch (PluginUnavailableException ex)
        {
            foreach (var line in ex.Output)
            {
                context.Error.WriteLine($"  | {line}");
            }

            throw;
        }
    }

    private static PluginManifest LoadManifest(string folder)
    {
        try
        {
            return PluginManifest.Load(folder);
        }
        catch (PluginManifestException ex)
        {
            throw new ConfigException(ex.Message, [.. ex.Problems.Select(p => new ConfigIssue(ConfigIssueSeverity.Error, PluginProtocol.ManifestFile, p))]);
        }
    }

    private static List<PluginJson> Rows(CliContext context)
    {
        var rows = new List<PluginJson> { new("whisper", "Whisper", null, "built-in", null, BuiltInState) };
        var config = context.TryLoadConfig();
        if (config is null)
        {
            return rows;
        }

        var trust = new PluginTrustStore(context.Paths.PluginTrustFile, context.Log);
        foreach (var (id, registration) in config.Plugins)
        {
            var (name, version, state) = Inspect(trust, id, registration.Path);
            rows.Add(new PluginJson(id, name, version, "custom", registration.Path, state));
        }

        return rows;
    }

    /// <summary>
    /// What the listing knows of one registered plugin; a plugin that cannot be read is named by its id, and why is its
    /// state, so one registration never stops the listing of the others.
    /// </summary>
    private static (string Name, string? Version, string State) Inspect(PluginTrustStore trust, string id, string path)
    {
        try
        {
            var folder = Normalize(path);
            if (!Directory.Exists(folder))
            {
                return (id, null, "the folder is missing");
            }

            var manifest = PluginManifest.Load(folder);
            var state = trust.Check(id, folder, manifest.ExecutablePath(folder)).Trust switch
            {
                PluginTrust.Confirmed => "confirmed",
                PluginTrust.Changed => "changed since you added it",
                _ => "not confirmed on this computer",
            };
            return (manifest.Name, manifest.Version, state);
        }
        catch (PluginManifestException ex)
        {
            return (id, null, $"plugin.json is not valid: {string.Join("; ", ex.Problems)}");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A path no system can name, such as one with a null in it, or a folder the user may not read.
            return (id, null, $"plugin.json cannot be read: {ex.Message}");
        }
    }

    /// <summary>Spelt as <see cref="PluginResolver"/> spells it, so the CLI and the daemon agree on the folder.</summary>
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
