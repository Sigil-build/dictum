using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core.Pipeline;

namespace Dictum.Cli.Commands;

/// <summary>Prints a transcription: the text on stdout, or the JSON schema with <c>--json</c>.</summary>
internal static class Results
{
    public static int Print(CliContext context, DictationResult result, bool json)
    {
        if (json)
        {
            context.Out.WriteLine(JsonSerializer.Serialize(TranscriptionJson.From(result), CliJsonContext.Readable.TranscriptionJson));
            return ExitCodes.Success;
        }

        if (result.Skipped || result.Text.Length == 0)
        {
            context.Error.WriteLine("No speech detected.");
            return ExitCodes.Success;
        }

        context.Out.WriteLine(result.Text);
        return ExitCodes.Success;
    }
}
