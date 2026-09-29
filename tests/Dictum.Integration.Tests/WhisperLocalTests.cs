using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Diagnostics;
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

    [Fact]
    public async Task TranscribesEnglish()
    {
        SkipUnlessEnabled();

        var text = await TranscribeAsync("en.wav", "en");

        Assert.Contains("country", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribesUkrainian()
    {
        SkipUnlessEnabled();
        Assert.SkipWhen(Path.GetFileName(Model!).Contains(".en.", StringComparison.Ordinal), "English-only model");

        var text = await TranscribeAsync("uk.wav", "uk");

        Assert.Contains("тестовий запис", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void SkipUnlessEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_INTEGRATION") == "1", "Set DICTUM_INTEGRATION=1 to run");
        Assert.SkipWhen(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set DICTUM_TEST_MODEL to an existing ggml model file");
    }

    private static async Task<string> TranscribeAsync(string fixture, string language)
    {
        var temp = Path.Combine(Path.GetTempPath(), "dictum-integration", Guid.NewGuid().ToString("N"));
        await using var host = DictumHost.Create(new AppPaths(temp, temp), NullLog.Instance, config: null);
        var profile = host.ResolveProfile(new ProfileOverrides(Model: Path.GetFullPath(Model!), Language: language));

        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture));
        var clip = Wav.ReadClip(stream);
        var result = await host.CreatePipeline().RunAsync(clip, profile, output: null, TestContext.Current.CancellationToken);
        return result.Text;
    }
}
