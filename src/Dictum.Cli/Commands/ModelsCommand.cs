using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core.Configuration;
using Dictum.Host;
using Dictum.Setup;

namespace Dictum.Cli.Commands;

internal static class ModelsCommand
{
    public static Command Create()
    {
        var command = new Command("models", "List and download the speech models Dictum knows about");

        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var all = new Option<bool>("--all") { Description = "Include models meant for tests" };
        var list = new Command("list", "Show catalog models, whether they are installed, and other model files in the models directory") { json, all };
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var rows = Rows(context.Paths, parse.GetValue(all));
            if (parse.GetValue(json))
            {
                context.Out.WriteLine(JsonSerializer.Serialize(rows, CliJsonContext.Readable.ListModelJson));
                return Task.FromResult(ExitCodes.Success);
            }

            foreach (var row in rows)
            {
                var state = row.InCatalog ? (row.Installed ? "installed" : "-") : "present";
                var id = row.InCatalog ? row.Id : "(not in catalog)";
                context.Out.WriteLine($"{state,-9}  {id,-28} {SetupRunner.Format(row.Size),8}  {row.Title}");
            }

            context.Error.WriteLine($"Models directory: {context.Paths.ModelsDirectory}");
            return Task.FromResult(ExitCodes.Success);
        }, ct));

        var ids = new Argument<string[]>("ids") { Description = "Catalog ids, e.g. whisper-small-q8", Arity = ArgumentArity.OneOrMore };
        var pull = new Command("pull", "Download models into the models directory (resumes after interruptions, checks SHA-256)") { ids };
        pull.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var entries = (parse.GetValue(ids) ?? []).Select(Catalog.BuiltIn.Get).ToList();
            using var http = FileDownloader.CreateHttpClient(CliApp.UserAgent);
            using var view = new SetupProgressView(context.Error, redraw: !Console.IsErrorRedirected);
            var runner = new SetupRunner(new FileDownloader(http), context.Paths);
            await runner.DownloadAsync(entries, HardwareProbe.Detect(context.Paths).FreeDiskBytes, view, token).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                context.Out.WriteLine(context.Paths.ResolveModel(entry.File));
            }

            return ExitCodes.Success;
        }, ct));

        var deleteIds = new Argument<string[]>("ids") { Description = "Catalog ids of downloaded models", Arity = ArgumentArity.OneOrMore };
        var delete = new Command("delete", "Delete downloaded models that no profile uses") { deleteIds };
        delete.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var entries = (parse.GetValue(deleteIds) ?? []).Select(Catalog.BuiltIn.Get).ToList();
            var config = context.TryLoadConfig();
            foreach (var entry in entries)
            {
                if (ModelUse.ProfilesUsing(config, context.Paths, entry) is { Count: > 0 } profiles)
                {
                    throw new ConfigException($"{entry.Id} is used by profile(s) {string.Join(", ", profiles)}; choose another model there first.");
                }
            }

            foreach (var entry in entries)
            {
                var file = context.Paths.ResolveModel(entry.File);
                var found = false;
                foreach (var path in new[] { file, FileDownloader.PartialPath(file) }.Where(File.Exists))
                {
                    File.Delete(path);
                    found = true;
                }

                if (found)
                {
                    context.Out.WriteLine(file);
                }
                else
                {
                    context.Error.WriteLine($"{entry.Id} is not installed");
                }
            }

            return Task.FromResult(ExitCodes.Success);
        }, ct));

        command.Add(list);
        command.Add(pull);
        command.Add(Folder());
        command.Add(delete);
        return command;
    }

    /// <summary>A folder the user names for downloads: an absolute path, created when only the folder itself is missing.</summary>
    /// <exception cref="ConfigException">The path is relative, or its parent folder does not exist either.</exception>
    internal static string ChosenFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            throw new ConfigException($"The downloads folder must be an absolute path: {folder}");
        }

        if (!Directory.Exists(folder))
        {
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is not { } parent || !Directory.Exists(parent))
            {
                throw new ConfigException($"{folder} does not exist, and neither does the folder it would go in; choose a folder on a connected drive.");
            }

            Directory.CreateDirectory(folder);
        }

        return folder;
    }

    private static Command Folder()
    {
        var path = new Argument<string?>("path") { Description = "The new downloads folder, an absolute path; created when its parent exists", Arity = ArgumentArity.ZeroOrOne };
        var move = new Option<bool>("--move") { Description = "Move the models already downloaded into the new folder" };
        var reset = new Option<bool>("--reset") { Description = "Use the data folder again" };
        var folder = new Command("folder", "Print the folder that downloads go to, or choose another") { path, move, reset };
        folder.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var requested = parse.GetValue(path);
            var resetting = parse.GetValue(reset);
            if (requested is null && !resetting)
            {
                context.Out.WriteLine(context.Paths.DownloadsDirectory);
                return ExitCodes.Success;
            }

            if (requested is not null && resetting)
            {
                throw new ConfigException("Give a folder or --reset, not both.");
            }

            var config = context.TryLoadConfig() ?? throw new ConfigException("No config yet; run 'dictum init --downloads-dir <path>' instead.");
            var target = resetting ? null : ChosenFolder(requested!);
            var next = context.Paths.WithDownloads(target);
            DownloadsMove plan;
            try
            {
                plan = DownloadsMover.Plan(context.Paths, next.DownloadsDirectory);
            }
            catch (ArgumentException ex)
            {
                throw new ConfigException(ex.Message);
            }

            var moving = parse.GetValue(move) && plan.Files.Count > 0;
            if (moving)
            {
                context.Error.WriteLine($"Moving {plan.Files.Count} file(s), {SetupRunner.Format(plan.Bytes)}, to {plan.To}");
                using var view = new SetupProgressView(context.Error, redraw: !Console.IsErrorRedirected);
                var progress = new SyncProgress<long>(copied => view.Bar(copied, plan.Bytes));
                await DownloadsMover.CopyAsync(plan, HardwareProbe.FreeDiskBytesAt(next.DownloadsDirectory), progress, token).ConfigureAwait(false);
            }

            // The copy is complete before the config names the new folder, and the old files go only after the save.
            context.SaveValidated(config with { General = config.General with { DownloadsDirectory = target } });
            context.Error.WriteLine($"Downloads folder: {next.DownloadsDirectory}");
            if (moving)
            {
                if (DownloadsMover.DeleteSources(plan) is { Count: > 0 } left)
                {
                    context.Error.WriteLine($"warning: these could not be deleted from {plan.From}: {string.Join(", ", left)}");
                }
            }
            else if (plan.Files.Count > 0)
            {
                context.Error.WriteLine(
                    $"{plan.Files.Count} file(s), {SetupRunner.Format(plan.Bytes)}, stay in {plan.From}, where Dictum no longer looks; move them to {plan.To} to use them.");
            }

            return ExitCodes.Success;
        }, ct));

        return folder;
    }

    private static List<ModelJson> Rows(Core.AppPaths paths, bool includeHidden)
    {
        var rows = Catalog.BuiltIn.Entries
            .Where(e => includeHidden || !e.Hidden)
            .Select(e => new ModelJson(e.Id, e.Kind.ToString().ToLowerInvariant(), e.Title, e.File, e.Size, ModelFiles.IsInstalled(paths, e), InCatalog: true))
            .ToList();
        if (Directory.Exists(paths.ModelsDirectory))
        {
            rows.AddRange(Directory.EnumerateFiles(paths.ModelsDirectory)
                .Where(f => Path.GetExtension(f) is ".bin" or ".gguf" && Catalog.BuiltIn.ForFile(f) is null)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => new ModelJson(Path.GetFileName(f), "file", Path.GetFileName(f), Path.GetFileName(f), new FileInfo(f).Length, Installed: true, InCatalog: false)));
        }

        return rows;
    }
}
