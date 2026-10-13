using Dictum.Core.Vocabulary;

namespace Dictum.Core.Tests.Vocabulary;

public class VocabularyTests
{
    [Fact]
    public void NormalizeTrimsSkipsEmptyAndCaseInsensitiveDuplicatesAndKeepsOrder() =>
        Assert.Equal(["Notepad++", "VS Code", "тогл"], VocabularyList.Normalize([" Notepad++ ", "", "VS Code", "notepad++", "тогл", "ТОГЛ"]));

    [Fact]
    public void MergeAppendsOnlyNewTerms() =>
        Assert.Equal(["Dictum", "VS Code", "Docker"], VocabularyList.Merge(["Dictum", "VS Code"], ["vs code", "Docker"]));

    [Fact]
    public void RemoveIsCaseInsensitiveAndReportsMissingTerms()
    {
        var (remaining, notFound) = VocabularyList.Remove(["Dictum", "VS Code", "Docker"], ["vs code", "Kubernetes"]);

        Assert.Equal(["Dictum", "Docker"], remaining);
        Assert.Equal(["Kubernetes"], notFound);
    }

    [Fact]
    public void RenderJoinsWithCommasOrReturnsNull()
    {
        Assert.Equal("Notepad++, VS Code", VocabularyList.Render(["Notepad++", "VS Code"]));
        Assert.Null(VocabularyList.Render([]));
        Assert.Null(VocabularyList.Render(null));
    }

    [Fact]
    public void ParseLinesSkipsBlanksAndComments() =>
        Assert.Equal(["Notepad++", "VS Code"], VocabularyList.ParseLines("# my words\r\nNotepad++\r\n\r\n  VS Code  \n"));

    [Fact]
    public void EmptyHintIsNull()
    {
        var hint = SttPromptBuilder.Build(null, []);

        Assert.Null(hint.Prompt);
        Assert.Empty(hint.Dropped);
    }

    [Fact]
    public void PromptAloneIsKeptAsIs() =>
        Assert.Equal("Avalonia, .NET", SttPromptBuilder.Build(" Avalonia, .NET ", []).Prompt);

    [Fact]
    public void VocabularyBecomesACommaListAfterThePrompt()
    {
        Assert.Equal("Notepad++, VS Code.", SttPromptBuilder.Build(null, ["Notepad++", "VS Code"]).Prompt);
        Assert.Equal("Avalonia Notepad++, VS Code.", SttPromptBuilder.Build("Avalonia", ["Notepad++", "VS Code"]).Prompt);
    }

    [Fact]
    public void TermsBeyondTheBudgetAreDroppedFromTheEnd()
    {
        // Four 98-character terms make "a…, b…, c…, d…." = 4 * 98 + 3 * 2 + 1 = 399 characters.
        string[] terms = [new('a', 98), new('b', 98), new('c', 98), new('d', 98), new('e', 98)];

        var hint = SttPromptBuilder.Build(null, terms);

        Assert.Equal(399, hint.Prompt!.Length);
        Assert.Equal([new string('e', 98)], hint.Dropped);
    }

    [Fact]
    public void ThePromptCountsTowardsTheBudget()
    {
        string[] terms = [new('a', 98), new('b', 98), new('c', 98), new('d', 98)];

        var hint = SttPromptBuilder.Build("Avalonia", terms);

        Assert.True(hint.Prompt!.Length <= SttPromptBuilder.MaxLength);
        Assert.Equal([new string('d', 98)], hint.Dropped);
    }

    [Fact]
    public void OnceATermDoesNotFitLaterShortTermsAreDroppedToo()
    {
        string[] terms = [new('a', 390), new('b', 20), "x"];

        var hint = SttPromptBuilder.Build(null, terms);

        Assert.Equal(new string('a', 390) + ".", hint.Prompt);
        Assert.Equal([new string('b', 20), "x"], hint.Dropped);
    }
}
