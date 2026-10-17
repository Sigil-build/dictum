using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

public sealed class ReplaceRulesCsvTests
{
    [Fact]
    public void FindAndReplaceAreEnough()
    {
        var (rules, problems) = ReplaceRulesCsv.Parse("кубер,Kubernetes\nдот нет,.NET\n");

        Assert.Empty(problems);
        Assert.Equal([("кубер", "Kubernetes", true, false), ("дот нет", ".NET", true, false)],
            rules.Select(r => (r.From, r.To, r.IgnoreCase, r.WholeWord)));
    }

    // Review focus 3.
    [Fact]
    public void ExcelStyleFilesImport()
    {
        const string csv = """
            Find;Replace;Match case;Whole word

            "мейн";"main";ні;так
            "a ""quoted"" one";"b; c";yes;0
            """;

        var (rules, problems) = ReplaceRulesCsv.Parse(csv);

        Assert.Empty(problems);
        Assert.Equal([("мейн", "main", true, true), ("a \"quoted\" one", "b; c", false, false)],
            rules.Select(r => (r.From, r.To, r.IgnoreCase, r.WholeWord)));
    }

    // Review focus 3.
    [Fact]
    public void BadLinesAreNamedAndSkipped()
    {
        var (rules, problems) = ReplaceRulesCsv.Parse("ok,OK\n,nothing\n\"open,quote\n");

        Assert.Equal(["ok"], rules.Select(r => r.From));
        Assert.Equal(["Line 2: nothing to find.", "Line 3: a quote is not closed."], problems);
    }
}
