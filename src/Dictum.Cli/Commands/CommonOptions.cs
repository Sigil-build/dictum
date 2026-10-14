using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Dictum.Host;

namespace Dictum.Cli.Commands;

/// <summary>Options shared by commands that transcribe: which profile, provider, language and processors.</summary>
internal sealed class TranscriptionOptions
{
    public Option<string?> Profile { get; } = new("--profile", "-p") { Description = "Profile id from the config (default: the first profile)" };

    public Option<string?> Stt { get; } = new("--stt") { Description = "STT provider id from the config" };

    public Option<string?> Model { get; } = new("--model", "-m") { Description = "Whisper model file (name in the models directory, or a path); needs no config" };

    public Option<string?> Language { get; } = new("--lang", "-l") { Description = "Language code such as uk, a list such as \"uk, en\" to pick from, or auto" };

    public Option<string?> Pipeline { get; } = new("--pipeline") { Description = "Comma-separated processor ids, replacing the profile's chain" };

    public Option<bool> Raw { get; } = new("--raw") { Description = "Skip all processing" };

    public Option<bool> Json { get; } = new("--json") { Description = "Print a JSON object with text, language, provider and timings" };

    public void AddTo(Command command)
    {
        command.Add(Profile);
        command.Add(Stt);
        command.Add(Model);
        command.Add(Language);
        command.Add(Pipeline);
        command.Add(Raw);
        command.Add(Json);
    }

    public ProfileOverrides Read(ParseResult parse) => new(
        Profile: parse.GetValue(Profile),
        Stt: parse.GetValue(Stt),
        Model: parse.GetValue(Model),
        Language: parse.GetValue(Language),
        Pipeline: SplitList(parse.GetValue(Pipeline)),
        Raw: parse.GetValue(Raw));

    public static IReadOnlyList<string>? SplitList(string? value) =>
        value is null ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class Durations
{
    /// <summary>Parses "30s", "1.5m", "500ms" or a plain number of seconds.</summary>
    public static bool TryParse(string text, out TimeSpan value)
    {
        value = default;
        text = text.Trim().ToLowerInvariant();
        (string number, double scale) = text switch
        {
            _ when text.EndsWith("ms", StringComparison.Ordinal) => (text[..^2], 0.001),
            _ when text.EndsWith('s') => (text[..^1], 1.0),
            _ when text.EndsWith('m') => (text[..^1], 60.0),
            _ => (text, 1.0),
        };

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(amount * scale);
        return true;
    }

    public static Option<TimeSpan?> Option(string name, string description) => new(name)
    {
        Description = description,
        CustomParser = Parse,
    };

    private static TimeSpan? Parse(ArgumentResult result)
    {
        var token = result.Tokens.Count > 0 ? result.Tokens[0].Value : string.Empty;
        if (TryParse(token, out var value))
        {
            return value;
        }

        result.AddError($"'{token}' is not a duration; use e.g. 30s, 1.5m or 500ms.");
        return null;
    }
}
