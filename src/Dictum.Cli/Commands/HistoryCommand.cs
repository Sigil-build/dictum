using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;

namespace Dictum.Cli.Commands;

internal static class HistoryCommand
{
    private const int DefaultCount = 10;

    public static Command Create()
    {
        var last = new Option<int>("--last", "-n") { Description = "How many entries", DefaultValueFactory = _ => DefaultCount };
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("history", "Show recent transcriptions") { last, json };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var entries = context.Host().History.ReadLast(parse.GetValue(last)).ToList();
            if (parse.GetValue(json))
            {
                context.Out.WriteLine(JsonSerializer.Serialize(entries, CliJsonContext.Readable.ListHistoryEntry));
                return Task.FromResult(ExitCodes.Success);
            }

            foreach (var e in entries)
            {
                var error = e.Error is null ? "" : $"  [failed: {e.Error}]";
                context.Out.WriteLine($"{e.At.LocalDateTime:yyyy-MM-dd HH:mm}  {e.ProfileId}/{e.Provider}  {e.Text}{error}");
            }

            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return command;
    }
}
