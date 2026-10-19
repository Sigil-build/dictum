using System.Text;
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

    [Fact]
    public void BadLinesAreNamedAndSkipped()
    {
        var (rules, problems) = ReplaceRulesCsv.Parse("ok,OK\n,nothing\n\"open,quote\n");

        Assert.Equal(["ok"], rules.Select(r => r.From));
        Assert.Equal(["Line 2: nothing to find.", "Line 3: a quote is not closed."], problems);
    }

    private static readonly Encoding Cp1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;

    // Excel's plain "CSV (comma delimited)" on a Ukrainian Windows is Windows-1251.
    [Fact]
    public void AFileInTheAnsiCodePageReadsAsCyrillic()
    {
        var bytes = Cp1251.GetBytes("мейн,main\nпуш,push\n");

        Assert.Equal("мейн,main\nпуш,push\n", ReplaceRulesCsv.Decode(bytes, Cp1251));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUtf8FileStaysUtf8(bool bom)
    {
        byte[] bytes = [.. bom ? Encoding.UTF8.Preamble.ToArray() : [], .. Encoding.UTF8.GetBytes("мейн,main")];

        Assert.Equal("мейн,main", ReplaceRulesCsv.Decode(bytes, Cp1251));
    }

    [Fact]
    public void AUtf16FileWithABomReads()
    {
        byte[] bytes = [.. Encoding.Unicode.Preamble.ToArray(), .. Encoding.Unicode.GetBytes("мейн,main")];

        Assert.Equal("мейн,main", ReplaceRulesCsv.Decode(bytes, Cp1251));
    }
}
