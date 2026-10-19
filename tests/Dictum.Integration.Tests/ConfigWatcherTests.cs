using System.Threading.Channels;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;

namespace Dictum.Integration.Tests;

public sealed class ConfigWatcherTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Several times the watcher's debounce: a reload that has not come by then is not coming.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-watch", Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly Channel<DictumConfig> _changed = Channel.CreateUnbounded<DictumConfig>();
    private readonly Channel<ConfigException> _invalid = Channel.CreateUnbounded<ConfigException>();
    private readonly ConfigStore _store;

    public ConfigWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        _store.Save(Config("first"));
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

    [Fact]
    public async Task ABurstOfSavesIsReloadedOnceWithTheLastOne()
    {
        using var watcher = Watch();

        for (var i = 0; i < 5; i++)
        {
            _store.Save(Config($"save{i}"));
        }

        Assert.Equal("save4", (await NextAsync(_changed)).Profiles[0].Id);
        await Task.Delay(QuietPeriod, _ct);
        Assert.False(_changed.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AnInvalidFileIsReportedAndTheNextValidOneIsApplied()
    {
        using var watcher = Watch();

        await File.WriteAllTextAsync(_store.Path, "{ not json", _ct);
        Assert.Contains("not valid JSON", (await NextAsync(_invalid)).Message);

        _store.Save(Config("fixed"));
        Assert.Equal("fixed", (await NextAsync(_changed)).Profiles[0].Id);
    }

    [Fact]
    public async Task AnEditorThatSavesThroughARenameIsSeen()
    {
        using var watcher = Watch();
        var swap = Path.Combine(_dir, ".config.json.swp");

        await File.WriteAllTextAsync(swap, ConfigStore.Serialize(Config("renamed")), _ct);
        File.Move(swap, _store.Path, overwrite: true);

        Assert.Equal("renamed", (await NextAsync(_changed)).Profiles[0].Id);
    }

    private ConfigWatcher Watch() => new(
        _store,
        (config, _) =>
        {
            _changed.Writer.TryWrite(config);
            return Task.CompletedTask;
        },
        invalid =>
        {
            _invalid.Writer.TryWrite(invalid);
            return Task.CompletedTask;
        },
        NullLog.Instance);

    private async Task<T> NextAsync<T>(Channel<T> channel) => await channel.Reader.ReadAsync(_ct).AsTask().WaitAsync(Patience, _ct);

    private static DictumConfig Config(string profile) => new()
    {
        Stt = new() { ["s"] = new WhisperLocalSttConfig { Model = "m.bin" } },
        Profiles = [new ProfileConfig { Id = profile, Stt = "s" }],
    };
}
