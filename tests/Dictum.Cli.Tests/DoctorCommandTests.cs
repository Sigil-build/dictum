using System.Globalization;
using System.Text.Json;

namespace Dictum.Cli.Tests;

/// <summary>
/// <c>dictum doctor</c> in the sandbox, which has no config and no daemon: each row that is not OK links to its section
/// of the troubleshooting page, in the UI language.
/// </summary>
public sealed class DoctorCommandTests(CliSandbox sandbox)
{
    private const string Troubleshooting = "https://docs.sigil.build/dictum/troubleshooting/";
    /// <summary>What comes before a row's hint and link: the label's and the name's columns, then a space.</summary>
    private static readonly string Indent = new(' ', 5 + 22 + 1);

    private static readonly string[] Labels = ["  OK ", "WARN ", " ERR "];

    [Fact]
    public async Task ARowThatIsNotOkLinksToItsSectionAfterTheHint()
    {
        var lines = Lines(await RunAsync("en-US", "doctor"));

        var config = Array.FindIndex(lines, line => line.StartsWith("WARN config ", StringComparison.Ordinal));
        Assert.True(config >= 0, string.Join('\n', lines));
        Assert.Equal($"     {"",-22} → Run 'dictum init'.", lines[config + 1]);
        Assert.Equal($"     {"",-22} see: {Troubleshooting}#config", lines[config + 2]);

        // One link per row that is not OK, and every one into the troubleshooting page.
        var links = lines.Where(line => line.StartsWith(Indent + "see: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(lines.Count(line => line.StartsWith("WARN ", StringComparison.Ordinal) || line.StartsWith(" ERR ", StringComparison.Ordinal)), links.Count);
        Assert.All(links, link => Assert.StartsWith(Indent + "see: " + Troubleshooting + "#", link, StringComparison.Ordinal));
        Assert.All(Rows(lines).Where(row => row.Label == "  OK "), row => Assert.False(row.Linked, row.Line));
    }

    [Fact]
    public async Task JsonGivesTheLinkOfEveryRowThatIsNotOk()
    {
        using var report = JsonDocument.Parse(await RunAsync("en-US", "doctor", "--json"));

        var checks = report.RootElement.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal($"{Troubleshooting}#config", checks.Single(c => c.GetProperty("name").GetString() == "config").GetProperty("docs").GetString());
        Assert.All(checks, check =>
        {
            var ok = string.Equals(check.GetProperty("level").GetString(), "ok", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(!ok, check.TryGetProperty("docs", out _));
        });
    }

    [Fact]
    public async Task WithoutAConfigTheSystemLanguageChoosesThePages()
    {
        using var report = JsonDocument.Parse(await RunAsync("uk-UA", "doctor", "--json"));

        Assert.Equal("https://docs.sigil.build/uk/dictum/troubleshooting/#config", Docs(report, "config"));
    }

    [Fact]
    public async Task TheConfigsUiLanguageChoosesThePages()
    {
        var file = sandbox.File("ukrainian-ui.json");
        await File.WriteAllTextAsync(file, """{ "schemaVersion": 1, "general": { "uiLanguage": "uk" } }""", TestContext.Current.CancellationToken);

        using var report = JsonDocument.Parse(await RunAsync("en-US", "doctor", "--json", "--config", file));

        Assert.Null(Docs(report, "config"));
        Assert.Equal("https://docs.sigil.build/uk/dictum/troubleshooting/#daemon", Docs(report, "daemon"));
    }

    /// <summary>Runs the CLI with the OS UI language <paramref name="culture"/>, and returns what it printed.</summary>
    private static async Task<string> RunAsync(string culture, params string[] args)
    {
        var saved = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            using var console = new ConsoleCapture();
            await console.RunAsync(args);
            return console.Out.ToString();
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
    }

    private static string[] Lines(string output) => output.ReplaceLineEndings("\n").Split('\n');

    /// <summary>The check rows, each with whether a link follows it before the next row.</summary>
    private static IEnumerable<(string Label, string Line, bool Linked)> Rows(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (Labels.FirstOrDefault(label => lines[i].StartsWith(label, StringComparison.Ordinal)) is { } label)
            {
                var next = lines.Skip(i + 1).TakeWhile(line => line.StartsWith(Indent, StringComparison.Ordinal));
                yield return (label, lines[i], next.Any(line => line.StartsWith(Indent + "see: ", StringComparison.Ordinal)));
            }
        }
    }

    private static string? Docs(JsonDocument report, string check) =>
        report.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == check)
            .TryGetProperty("docs", out var docs) ? docs.GetString() : null;
}
