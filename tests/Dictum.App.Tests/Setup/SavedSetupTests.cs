using Dictum.App.ViewModels.Setup;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Setup;

public sealed class SavedSetupTests : IDisposable
{
    private readonly SetupFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static SavedSetup Sample() => new(
        "uk, en", "whisper-large-v3-turbo-q8", true, "http://localhost:8080/v1", "qwen2.5:7b", "env:LLM_KEY", false,
        ["Nexus5"], true, ["whisper-large-v3-turbo-q8", "vad-silero-6.2"]);

    [Fact]
    public void AKeptWalkReadsBackTheSame()
    {
        SavedSetupFile.Save(_fx.Paths, Sample(), NullLog.Instance);

        var read = SavedSetupFile.Load(_fx.Paths, NullLog.Instance);

        Assert.NotNull(read);
        Assert.Equal(Sample() with { Words = read.Words, Files = read.Files }, read);
        Assert.Equal(Sample().Words, read.Words);
        Assert.Equal(Sample().Files, read.Files);
    }

    // A damaged or foreign setup.json is ignored and removed; the walk starts over.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 2, "language": "uk", "url": "", "model": "", "words": [], "files": [] }""")]
    [InlineData("""{ "version": 1 }""")]
    public void ADamagedSavedWalkIsIgnored(string text)
    {
        Directory.CreateDirectory(_fx.Paths.DataDirectory);
        File.WriteAllText(_fx.Paths.SetupFile, text);

        Assert.Null(SavedSetupFile.Load(_fx.Paths, NullLog.Instance));
        Assert.False(File.Exists(_fx.Paths.SetupFile));
    }

    // A walk kept before the folder could be chosen reads as the data folder; the version stays 1.
    [Fact]
    public void TheDownloadsFolderRoundTripsAndAnOldFileReadsAsNone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dictum-downloads");
        SavedSetupFile.Save(_fx.Paths, Sample() with { DownloadsDirectory = folder }, NullLog.Instance);

        Assert.Equal(folder, SavedSetupFile.Load(_fx.Paths, NullLog.Instance)?.DownloadsDirectory);

        File.WriteAllText(_fx.Paths.SetupFile, """{ "version": 1, "language": "uk", "url": "", "model": "", "words": [], "files": [] }""");

        var old = SavedSetupFile.Load(_fx.Paths, NullLog.Instance);
        Assert.NotNull(old);
        Assert.Null(old.DownloadsDirectory);
    }

    [Fact]
    public void NoFileIsNoWalk() => Assert.Null(SavedSetupFile.Load(_fx.Paths, NullLog.Instance));
}
