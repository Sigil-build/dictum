using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Setup;
using Dictum.Testing;

namespace Dictum.Host.Tests.Whisper;

/// <summary>
/// A missing model file's hint comes from the catalog the Host gives Whisper: the id to pull and where the file comes
/// from, as the models row of dictum doctor says. The check stops before Whisper loads anything.
/// </summary>
public sealed class WhisperDownloadHintTests : IDisposable
{
    private const string HandPlaced = "hand-placed.bin";

    private readonly TempDirectory _dir = new();
    private readonly AppPaths _paths;
    private readonly ProviderFactory _providers;

    public WhisperDownloadHintTests()
    {
        _paths = new AppPaths(_dir.Path, _dir.Path);
        _providers = new ProviderFactory(_paths, new FakeSecrets(), NullLog.Instance);
    }

    public void Dispose()
    {
        _providers.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task AMissingVadModelNamesItsCatalogIdAndSource()
    {
        Directory.CreateDirectory(_paths.ModelsDirectory);
        await File.WriteAllTextAsync(_paths.ResolveModel(HandPlaced), "", TestContext.Current.CancellationToken);
        var vad = Catalog.BuiltIn.Get(SetupPlanner.VadId);

        var status = await CheckAsync(new WhisperLocalSttConfig { Model = HandPlaced, Vad = true });

        Assert.Equal(
            HealthStatus.Error(
                $"VAD model not found: {_paths.ResolveModel(vad.File)}",
                $"Run 'dictum models pull {vad.Id}', which downloads {vad.Url}, or set \"vad\": false."),
            status);
    }

    [Fact]
    public async Task AMissingCatalogModelNamesItsCatalogIdAndSource()
    {
        var small = Catalog.BuiltIn.Get(SetupPlanner.CpuModel);

        var status = await CheckAsync(new WhisperLocalSttConfig { Model = small.File });

        Assert.Equal($"Run 'dictum models pull {small.Id}', which downloads {small.Url}.", status.Hint);
    }

    private Task<HealthStatus> CheckAsync(WhisperLocalSttConfig config) =>
        _providers.CreateStt("local", config).CheckAsync(TestContext.Current.CancellationToken);
}
