using Dictum.Processing;
using Dictum.Testing;

namespace Dictum.Core.Tests.Processing;

public sealed class PromptLibraryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>Not created up front: the library makes it when it writes.</summary>
    private string Folder => _temp.File("prompts");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheBuiltInsAreListedWithoutAFolder()
    {
        var prompts = new PromptLibrary(Folder);

        Assert.Equal(["cleanup", "code-comment", "email"], prompts.Names());
        Assert.True(PromptLibrary.IsBuiltIn("cleanup"));
        Assert.Contains("{{text}}", prompts.Read("cleanup"), StringComparison.Ordinal);
    }

    [Fact]
    public void AWrittenPromptIsListedReadAndOverridesABuiltIn()
    {
        var prompts = new PromptLibrary(Folder);

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
        var prompts = new PromptLibrary(Folder);
        prompts.Write("cleanup", "Fix {{text}}");

        prompts.Delete("cleanup");

        Assert.False(prompts.HasFile("cleanup"));
        Assert.NotEqual("Fix {{text}}", prompts.Read("cleanup"));
    }

    [Fact]
    public void AnUnknownPromptReadsEmpty() => Assert.Equal("", new PromptLibrary(Folder).Read("nothing-here"));

    [Theory]
    [InlineData("meeting-notes", true)]
    [InlineData("Нотатки", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData(" spaced ", false)]
    public void NamesMustBeFileNames(string name, bool valid) => Assert.Equal(valid, PromptLibrary.IsValidName(name));
}
