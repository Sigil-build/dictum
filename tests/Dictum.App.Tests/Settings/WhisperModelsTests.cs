using Dictum.App.Settings;
using Dictum.Core;
using Dictum.Setup;

namespace Dictum.App.Tests.Settings;

public sealed class WhisperModelsTests : IDisposable
{
    private const string CatalogJson = """
        {
          "version": 1,
          "entries": [
            { "id": "small", "kind": "whisper", "title": "Whisper small, 8-bit: fast on a CPU", "file": "ggml-small-q8_0.bin",
              "url": "https://example.org/small", "sha256": "00", "size": 5, "languages": ["*"], "licence": { "name": "MIT", "url": "https://example.org" } },
            { "id": "turbo", "kind": "whisper", "title": "Whisper large-v3-turbo, 8-bit: best quality, needs a GPU", "file": "ggml-large-v3-turbo-q8_0.bin",
              "url": "https://example.org/turbo", "sha256": "00", "size": 7, "languages": ["*"], "licence": { "name": "MIT", "url": "https://example.org" } },
            { "id": "vad", "kind": "vad", "title": "Silero voice activity detector", "file": "ggml-silero-v6.2.0.bin",
              "url": "https://example.org/vad", "sha256": "00", "size": 3, "languages": ["*"], "licence": { "name": "MIT", "url": "https://example.org" } }
          ]
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-models", Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;

    public WhisperModelsTests()
    {
        _paths = new AppPaths(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"));
        Directory.CreateDirectory(_paths.ModelsDirectory);
    }

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

    private void File(string name, int size) => System.IO.File.WriteAllBytes(Path.Combine(_paths.ModelsDirectory, name), new byte[size]);

    [Fact]
    public void InstalledCatalogModelsComeByTitleThenOtherFilesByName()
    {
        File("ggml-small-q8_0.bin", 5);
        File("ggml-large-v3-turbo-q8_0.bin", 2);
        File("ggml-silero-v6.2.0.bin", 3);
        File("my-finetune.bin", 1);

        var choices = WhisperModels.Choices(_paths, Catalog.Parse(CatalogJson), "ggml-small-q8_0.bin");

        Assert.Equal(
            [("ggml-small-q8_0.bin", "Whisper small, 8-bit: fast on a CPU"), ("ggml-large-v3-turbo-q8_0.bin", "ggml-large-v3-turbo-q8_0.bin"), ("my-finetune.bin", "my-finetune.bin")],
            choices.Select(c => (c.Value, c.Label)));
    }

    [Fact]
    public void AConfiguredFileThatIsMissingStaysAndSaysSo()
    {
        var choices = WhisperModels.Choices(_paths, Catalog.Parse(CatalogJson), "ggml-base.bin");

        Assert.Equal(("ggml-base.bin", "ggml-base.bin (not downloaded)"), (choices[^1].Value, choices[^1].Label));
    }

    [Fact]
    public void PresenceResolvesInTheModelsFolder()
    {
        File("ggml-silero-v6.2.0.bin", 3);

        Assert.True(WhisperModels.IsPresent(_paths, "ggml-silero-v6.2.0.bin"));
        Assert.False(WhisperModels.IsPresent(_paths, "ggml-base.bin"));
    }
}
