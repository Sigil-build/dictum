using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupDownloadsStepTests : IDisposable
{
    private readonly SetupFixture _fx = new();
    private readonly Catalog _catalog = ModelsPageTests.TestCatalog();

    public void Dispose() => _fx.Dispose();

    private CatalogEntry Turbo => _catalog.Get(ModelsPageTests.Turbo);

    private CatalogEntry Vad => _catalog.Get(SetupPlanner.VadId);

    private SetupDownloadsStep Load(IReadOnlyList<CatalogEntry> downloads, ExistingLlmServer? server = null, string? error = null, bool invalid = false)
    {
        var step = new SetupDownloadsStep(_fx.Services());
        step.Load(error is null ? new SetupPlan(DefaultConfig.Create(), downloads) : null, error, [Turbo, Vad], server, invalid);
        return step;
    }

    [Fact]
    public void TheTableListsTheMissingFiles()
    {
        var step = Load([Turbo, Vad]);

        Assert.Equal(
            [
                new DownloadRow("ggml-large-v3-turbo-q8_0.bin", "3 MB", "MIT", "huggingface.co/ggerganov/whisper.cpp"),
                new DownloadRow("ggml-silero-v6.2.0.bin", "0 MB", "MIT", "huggingface.co/ggml-org/whisper-vad"),
            ],
            step.Rows);
        Assert.Equal(("3 MB", true, true, (string?)null), (step.TotalText, step.HasFiles, step.CanContinue, step.PresentNote));
        Assert.Equal($"Files are fetched once, verified by SHA-256, and stored in {_fx.Paths.ModelsDirectory}.", step.FolderText);
        Assert.Equal("No audio or text ever leaves your computer.", step.PrivacyText);
    }

    [Fact]
    public void NextNeedsTheConsent()
    {
        var step = Load([Turbo, Vad]);

        step.Consent = false;

        Assert.False(step.CanContinue);
    }

    [Fact]
    public void TooLittleSpaceBlocksTheDownload()
    {
        _fx.FreeDisk = 1_000_000;

        var step = Load([Turbo, Vad]);

        Assert.Equal("Not enough free space: 203 MB needed, 1 MB free.", step.SpaceError);
        Assert.False(step.CanContinue);
    }

    [Fact]
    public void NothingToDownloadNamesWhatIsInPlace()
    {
        var step = Load([]);

        Assert.Equal((false, true), (step.HasFiles, step.CanContinue));
        Assert.Equal("Already on this machine, not downloaded again: Whisper large-v3-turbo, 8-bit (3 MB), Silero voice activity detector (0 MB).", step.PresentNote);
    }

    [Fact]
    public void ARemoteServerIsNamedInThePrivacyLine()
    {
        Assert.Equal("No audio leaves your computer; text goes to home-server for clean-up.",
            Load([Turbo], new ExistingLlmServer("http://home-server:11434/v1", "qwen2.5:7b", null)).PrivacyText);
        Assert.Equal("No audio or text ever leaves your computer.",
            Load([Turbo], new ExistingLlmServer("http://127.0.0.1:8080/v1", "qwen2.5:7b", null)).PrivacyText);
    }

    [Fact]
    public void APlanErrorBlocksNext()
    {
        var step = Load([], error: "The new config would be invalid.", invalid: true);

        Assert.Equal(("The new config would be invalid.", false), (step.Error, step.CanContinue));
        Assert.Equal("config.json could not be read; finishing the setup replaces it.", step.ConfigNote);
    }
}
