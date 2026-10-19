using Dictum.Processing;
using Dictum.Testing;

namespace Dictum.Core.Tests.Processing;

/// <summary>The daemon keeps one PromptTemplates per engine, so a changed prompt file must be seen without a reload.</summary>
public sealed class PromptTemplatesTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    // Final review C2: an edit made in Settings (or by hand) reaches the next dictation.
    [Fact]
    public void AnEditedPromptFileIsReadAgain()
    {
        var templates = new PromptTemplates(_dir.Path);
        File.WriteAllText(_dir.File("cleanup.md"), "Fix {{text}}");
        Assert.Equal("Fix {{text}}", templates.Load("cleanup"));

        File.WriteAllText(_dir.File("cleanup.md"), "Tidy up {{text}}");

        Assert.Equal("Tidy up {{text}}", templates.Load("cleanup"));
    }

    [Fact]
    public void AnEditOfTheSameLengthIsSeenByItsTime()
    {
        var templates = new PromptTemplates(_dir.Path);
        File.WriteAllText(_dir.File("notes.md"), "A {{text}}");
        File.SetLastWriteTimeUtc(_dir.File("notes.md"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("A {{text}}", templates.Load("notes"));

        File.WriteAllText(_dir.File("notes.md"), "B {{text}}");
        File.SetLastWriteTimeUtc(_dir.File("notes.md"), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("B {{text}}", templates.Load("notes"));
    }

    // Final review C2: "Restore the built-in prompt" deletes the file; the built-in comes back.
    [Fact]
    public void ADeletedOverrideBringsTheBuiltInBack()
    {
        var templates = new PromptTemplates(_dir.Path);
        var builtIn = templates.Load("cleanup");
        File.WriteAllText(_dir.File("cleanup.md"), "Fix {{text}}");
        Assert.Equal("Fix {{text}}", templates.Load("cleanup"));

        File.Delete(_dir.File("cleanup.md"));

        Assert.Equal(builtIn, templates.Load("cleanup"));
    }
}
