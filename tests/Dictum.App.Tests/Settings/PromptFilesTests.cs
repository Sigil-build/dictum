using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

public sealed class PromptFilesTests : IDisposable
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
        var prompts = new PromptFiles(_dir);

        Assert.Equal(["cleanup", "code-comment", "email"], prompts.Names());
        Assert.True(prompts.IsBuiltIn("cleanup"));
        Assert.Contains("{{text}}", prompts.Read("cleanup"), StringComparison.Ordinal);
    }

    [Fact]
    public void AWrittenPromptIsListedReadAndOverridesABuiltIn()
    {
        var prompts = new PromptFiles(_dir);

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
        var prompts = new PromptFiles(_dir);
        prompts.Write("cleanup", "Fix {{text}}");

        prompts.Delete("cleanup");

        Assert.False(prompts.HasFile("cleanup"));
        Assert.NotEqual("Fix {{text}}", prompts.Read("cleanup"));
    }

    [Fact]
    public void AnUnknownPromptReadsEmpty() => Assert.Equal("", new PromptFiles(_dir).Read("nothing-here"));

    [Theory]
    [InlineData("meeting-notes", true)]
    [InlineData("Нотатки", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData(" spaced ", false)]
    public void NamesMustBeFileNames(string name, bool valid) => Assert.Equal(valid, PromptFiles.IsValidName(name));
}
