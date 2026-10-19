using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host;

namespace Dictum.Integration.Tests;

/// <summary>
/// Real whisper.cpp runs. Enabled with DICTUM_INTEGRATION=1 and DICTUM_TEST_MODEL=&lt;path to a ggml model&gt;.
/// </summary>
/// <remarks>
/// Fixtures: en.wav is samples/jfk.wav from whisper.cpp (MIT). uk.wav was synthesised with Piper TTS,
/// voice uk_UA-ukrainian_tts-medium (trained on a CC0 dataset).
/// </remarks>
public class WhisperLocalTests
{
    private static readonly string? Model = Environment.GetEnvironmentVariable("DICTUM_TEST_MODEL");
    private static readonly string[] ListedLanguages = ["uk", "de"];

    [Fact]
    public async Task TranscribesEnglish()
    {
        SkipUnlessEnabled();

        var text = (await TranscribeAsync("en.wav", "en")).Text;

        Assert.Contains("country", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribesUkrainian()
    {
        SkipUnlessEnabled();
        Assert.SkipWhen(Path.GetFileName(Model!).Contains(".en.", StringComparison.Ordinal), "English-only model");

        var text = (await TranscribeAsync("uk.wav", "uk")).Text;

        Assert.Contains("тестовий запис", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en.wav", "en", "country")]
    [InlineData("uk.wav", "uk", "тестовий запис")]
    public async Task PicksEachLanguageFromTheList(string fixture, string expected, string phrase)
    {
        SkipUnlessEnabled();
        Assert.SkipWhen(Path.GetFileName(Model!).Contains(".en.", StringComparison.Ordinal), "English-only model");

        var result = await TranscribeAsync(fixture, "uk, en");

        Assert.Equal(expected, result.Language);
        Assert.Contains(phrase, result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectionStaysInsideTheList()
    {
        SkipUnlessEnabled();
        Assert.SkipWhen(Path.GetFileName(Model!).Contains(".en.", StringComparison.Ordinal), "English-only model");

        var result = await TranscribeAsync("en.wav", "uk, de");

        Assert.Contains(result.Language, ListedLanguages);
    }

    private static void SkipUnlessEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_INTEGRATION") == "1", "Set DICTUM_INTEGRATION=1 to run");
        Assert.SkipWhen(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set DICTUM_TEST_MODEL to an existing ggml model file");
    }

    private static async Task<DictationResult> TranscribeAsync(string fixture, string language)
    {
        var temp = Path.Combine(Path.GetTempPath(), "dictum-integration", Guid.NewGuid().ToString("N"));
        await using var host = DictumHost.Create(new AppPaths(temp, temp), NullLog.Instance, config: null);
        var profile = host.ResolveProfile(new ProfileOverrides(Model: Path.GetFullPath(Model!), Language: language));

        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture));
        var clip = Wav.ReadClip(stream);
        return await host.CreatePipeline().RunAsync(clip, profile, output: null, TestContext.Current.CancellationToken);
    }
}
