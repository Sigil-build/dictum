using System.CommandLine;
using System.Text;
using Dictum.Testing;

namespace Dictum.Cli.Tests.Docs;

/// <summary>
/// docs/reference/cli.md is generated from the command tree; these tests keep it in step with the program and keep the
/// tree describing itself well enough to generate from.
/// </summary>
public sealed class CliReferenceTests
{
    private const string WriteVariable = "DICTUM_WRITE_DOCS";

    private static string Page => Path.Combine(Repository.Root, "docs", "reference", "cli.md");

    private static string Regenerate =>
        $"{WriteVariable}=1 dotnet test --project tests/Dictum.Cli.Tests --filter-class \"*.CliReferenceTests\"";

    [Fact]
    public void EveryCommandOptionAndArgumentHasADescription()
    {
        var missing = new List<string>();
        Walk(CliApp.BuildRoot(), "dictum");
        Assert.True(missing.Count == 0, "No description: " + string.Join(", ", missing));

        void Walk(Command command, string path)
        {
            if (command.Hidden)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(command.Description))
            {
                missing.Add(path);
            }

            foreach (var option in command.Options.Where(o => !o.Hidden && o.Name is not ("--help" or "--version")))
            {
                if (string.IsNullOrWhiteSpace(option.Description))
                {
                    missing.Add($"{path} {option.Name}");
                }
            }

            foreach (var argument in command.Arguments.Where(a => !a.Hidden))
            {
                if (string.IsNullOrWhiteSpace(argument.Description))
                {
                    missing.Add($"{path} <{argument.Name}>");
                }
            }

            foreach (var child in command.Subcommands)
            {
                Walk(child, $"{path} {child.Name}");
            }
        }
    }

    [Fact]
    public void CommittedReferenceMatchesTheProgram()
    {
        var expected = CliReference.Render(CliApp.BuildRoot());
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Page)!);
            File.WriteAllText(Page, expected, new UTF8Encoding(false));
        }

        Assert.True(File.Exists(Page), "docs/reference/cli.md is missing: run " + Regenerate);
        var committed = File.ReadAllText(Page).ReplaceLineEndings("\n");
        Assert.True(committed == expected, "docs/reference/cli.md is out of date: run " + Regenerate + " and commit it");
    }

    [Fact]
    public void ReferenceListsNestedCommandsWithTheirArguments()
    {
        var page = CliReference.Render(CliApp.BuildRoot());

        Assert.Contains("## `dictum models pull` {#models-pull}", page);
        Assert.Contains("`dictum models pull <ids>...`", page);
        Assert.Contains("## `dictum init` {#init}", page);
        var init = Section(page, "init");
        Assert.Contains("`--gpu`", init);
        Assert.Contains("Values: auto, cpu", init);
        Assert.Contains("Default: 10", Section(page, "history"));
        Assert.DoesNotContain("config init", page);
    }

    [Fact]
    public void ReferenceHasUniqueHeadingIdsAndNoCarriageReturns()
    {
        var page = CliReference.Render(CliApp.BuildRoot());
        var headings = page.Split('\n').Where(l => l.StartsWith("##", StringComparison.Ordinal)).ToList();
        var ids = headings.Select(l => l[(l.LastIndexOf("{#", StringComparison.Ordinal) + 2)..^1]).ToList();

        Assert.All(headings, h => Assert.EndsWith("}", h));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain('\r', page);
    }

    private static string Section(string page, string id)
    {
        var start = page.IndexOf($"{{#{id}}}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No section {id}");
        var end = page.IndexOf("\n## ", start, StringComparison.Ordinal);
        return end < 0 ? page[start..] : page[start..end];
    }
}
