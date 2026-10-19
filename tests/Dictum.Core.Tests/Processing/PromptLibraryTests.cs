using Dictum.Processing;

namespace Dictum.Core.Tests.Processing;

public sealed class PromptLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-prompts", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheBuiltInsAreListedWithoutAFolder()
    {
        var prompts = new PromptLibrary(_dir);

        Assert.Equal(["cleanup", "code-comment", "email"], prompts.Names());
        Assert.True(PromptLibrary.IsBuiltIn("cleanup"));
        Assert.Contains("{{text}}", prompts.Read("cleanup"), StringComparison.Ordinal);
    }

    [Fact]
    public void AWrittenPromptIsListedReadAndOverridesABuiltIn()
    {
        var prompts = new PromptLibrary(_dir);

        prompts.Write("meeting-notes", "Make notes from {{text}}");
        prompts.Write("cleanup", "Fix {{text}}");

        Assert.Equal(["cleanup", "code-comment", "email", "meeting-notes"], prompts.Names());
        Assert.Equal("Make notes from {{text}}", prompts.Read("meeting-notes"));
        Assert.Equal("Fix {{text}}", prompts.Read("cleanup"));
        Assert.True(prompts.HasFile("cleanup"));
    }

    [Fact]
    public void DeletingAnOverrideBringsTheBuiltInBack()
    {
        var prompts = new PromptLibrary(_dir);
        prompts.Write("cleanup", "Fix {{text}}");

        prompts.Delete("cleanup");

        Assert.False(prompts.HasFile("cleanup"));
        Assert.NotEqual("Fix {{text}}", prompts.Read("cleanup"));
    }

    [Fact]
    public void AnUnknownPromptReadsEmpty() => Assert.Equal("", new PromptLibrary(_dir).Read("nothing-here"));

    [Theory]
    [InlineData("meeting-notes", true)]
    [InlineData("Нотатки", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData(" spaced ", false)]
    public void NamesMustBeFileNames(string name, bool valid) => Assert.Equal(valid, PromptLibrary.IsValidName(name));
}
