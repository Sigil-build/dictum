using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupModelStepTests : IDisposable
{
    private readonly SetupFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private SetupModelStep Show(string? kept = null, string? current = null)
    {
        var step = new SetupModelStep(_fx.Services(), kept, current) { Language = "uk, en" };
        step.OnShown();
        return step;
    }

    [Fact]
    public void TheGpuRecommendationIsPickedOnAFreshMachine()
    {
        var step = Show();

        Assert.Equal("Speech model", step.Name);
        Assert.Equal([ModelsPageTests.Turbo, ModelsPageTests.Medium, ModelsPageTests.Small], step.Choices.Select(c => c.Id));
        Assert.Equal(ModelsPageTests.Turbo, step.SpeechModel);
        var turbo = step.Choices[0];
        Assert.Equal(("Whisper large-v3-turbo, 8-bit", "best quality, needs a GPU", "3 MB", true, false),
            (turbo.Name, turbo.Note, turbo.Size, turbo.Recommended, turbo.Installed));
        Assert.Equal("~5.5 s for 5 s of speech (estimate)", turbo.Speed);
        Assert.Equal("GPU found: Intel(R) Iris(R) Xe Graphics · 15.8 GB shared · 31.6 GB RAM. Recommendations are based on it.", step.HardwareText);
        Assert.True(step.CanContinue);
    }

    [Fact]
    public void ACpuOnlyMachineRecommendsSmall()
    {
        _fx.Hardware = new HardwareInfo(null, 0);

        var step = Show();

        Assert.Equal(ModelsPageTests.Small, step.SpeechModel);
        Assert.Equal([false, false, true], step.Choices.Select(c => c.Recommended));
    }

    [Fact]
    public async Task UntilTheProbeAnswersThereIsNoRecommendation()
    {
        _fx.Probing = new TaskCompletionSource<HardwareInfo>();
        var step = Show();

        Assert.Equal("Looking for a GPU…", step.HardwareText);
        Assert.DoesNotContain(step.Choices, c => c.Recommended);

        _fx.Probing.SetResult(new HardwareInfo(null, 0));
        await step.ProbeAsync();

        Assert.Equal(ModelsPageTests.Small, step.SpeechModel);
    }

    [Fact]
    public async Task AModelTheUserPickedStaysPickedWhenTheProbeAnswers()
    {
        _fx.Probing = new TaskCompletionSource<HardwareInfo>();
        var step = Show();

        step.Selected = step.Choices.Single(c => c.Id == ModelsPageTests.Medium);
        _fx.Probing.SetResult(new HardwareInfo(null, 0));
        await step.ProbeAsync();

        Assert.Equal(ModelsPageTests.Medium, step.SpeechModel);
    }

    [Fact]
    public void AnInstalledModelSaysSo()
    {
        _fx.Install(ModelsPageTests.SmallFile, ModelsPageTests.SmallData);

        Assert.Equal([false, false, true], Show().Choices.Select(c => c.Installed));
    }

    [Fact]
    public void ARerunPicksTheCurrentModel() => Assert.Equal(ModelsPageTests.Medium, Show(current: ModelsPageTests.Medium).SpeechModel);

    [Fact]
    public void AHandPlacedFileIsOfferedFirstAndKept()
    {
        _fx.Install("my-model.bin", new byte[1_500_000]);

        var step = Show(kept: "my-model.bin");

        var kept = step.Choices[0];
        Assert.Equal(((string?)null, "my-model.bin", "the model your config uses now", "1.5 MB", true),
            (kept.Id, kept.Name, kept.Note, kept.Size, kept.Installed));
        Assert.Equal(kept, step.Selected);
        Assert.Null(step.SpeechModel);
    }

    // Final review I5: a screen reader reads a card by its model's name, not as a record.
    [Fact]
    public void ACardReadsAsItsModelsName() => Assert.Equal("Whisper large-v3-turbo, 8-bit", Show().Choices[0].ToString());

    // Final review M9: step 2 shows the speed class (spec 8 step 2, 10.2), as Settings → Models does.
    [Fact]
    public void EachCardShowsItsSpeedClass()
    {
        Assert.Equal(["Usable", "Usable", "Fast"], Show().Choices.Select(c => c.SpeedBadge));

        _fx.Hardware = new HardwareInfo(null, 0);
        var turbo = Show().Choices[0];

        Assert.Equal(("Too slow here", true), (turbo.SpeedBadge, turbo.TooSlow));
    }

    // PR 5b, review focus 5: a resumed walk's model wins over a hand-placed file and the config's model.
    [Fact]
    public void APickedModelWinsOverAHandPlacedFile()
    {
        var step = new SetupModelStep(_fx.Services(), "my-model.bin", ModelsPageTests.Small) { Language = "en" };

        step.Pick(ModelsPageTests.Medium);

        Assert.Equal(ModelsPageTests.Medium, step.SpeechModel);
    }
}
