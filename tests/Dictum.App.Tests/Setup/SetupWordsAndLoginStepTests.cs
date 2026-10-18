using Dictum.App.ViewModels.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupWordsAndLoginStepTests
{
    [Fact]
    public void APastedListSplitsOnLinesCommasAndSemicolons()
    {
        var step = new SetupWordsStep([]) { Text = "Nexus5\r\nAvalonia, Kubernetes; pull request\n\nnexus5\n" };

        Assert.Equal(["Nexus5", "Avalonia", "Kubernetes", "pull request"], step.Words);
        Assert.Equal(("Your words", true), (step.Name, step.CanContinue));
    }

    [Fact]
    public void ARerunShowsTheCurrentWords() => Assert.Equal("Nexus5\nAvalonia", new SetupWordsStep(["Nexus5", "Avalonia"]).Text);

    [Fact]
    public void StartAtLoginMirrorsItsTwoRadios()
    {
        var step = new SetupLoginStep(available: true, startAtLogin: true);
        Assert.Equal(("Start at login", true, true, false), (step.Name, step.IsAvailable, step.StartAtLogin, step.StartManually));

        step.StartManually = true;

        Assert.False(step.StartAtLogin);
    }

    [Fact]
    public void WithoutAutostartTheStepCannotTurnItOn()
    {
        var step = new SetupLoginStep(available: false, startAtLogin: true);

        Assert.Equal((false, false, true), (step.IsAvailable, step.StartAtLogin, step.CanContinue));
    }
}
