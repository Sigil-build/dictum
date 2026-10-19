using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;
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
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
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
        }));

        var ids = new Argument<string[]>("ids") { Description = "Catalog ids, e.g. whisper-small-q8", Arity = ArgumentArity.OneOrMore };
        var pull = new Command("pull", "Download models into the models directory (resumes after interruptions, checks SHA-256)") { ids };
        pull.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
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
        }));

        command.Add(list);
        command.Add(pull);
        return command;
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
