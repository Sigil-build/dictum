using System.Threading.Channels;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Testing;

namespace Dictum.Host.Tests;

public sealed class ConfigWatcherTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Several times the watcher's debounce: a reload that has not come by then is not coming.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);

    private readonly TempDirectory _dir = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly Channel<DictumConfig> _changed = Channel.CreateUnbounded<DictumConfig>();
    private readonly Channel<ConfigException> _invalid = Channel.CreateUnbounded<ConfigException>();
    private readonly ConfigStore _store;

    public ConfigWatcherTests()
    {
        _store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        _store.Save(Config("first"));
    }

    public void Dispose() => _dir.Dispose();

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
        var swap = _dir.File(".config.json.swp");

        await File.WriteAllTextAsync(swap, ConfigStore.Serialize(Config("renamed")), _ct);
        File.Move(swap, _store.Path, overwrite: true);

        Assert.Equal("renamed", (await NextAsync(_changed)).Profiles[0].Id);
    }

    [Fact]
    public async Task AReloadThatFailsForAnotherReasonIsLogged()
    {
        var log = new ErrorLog();
        using var watcher = new ConfigWatcher(
            _store,
            (_, _) => throw new InvalidOperationException("The engine could not be built."),
            _ => Task.CompletedTask,
            log);

        _store.Save(Config("next"));

        Assert.Contains("The engine could not be built.", await NextAsync(log.Errors));
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

    private sealed class ErrorLog : ILog
    {
        public Channel<string> Errors { get; } = Channel.CreateUnbounded<string>();

        public bool IncludeText => false;

        public void Write(LogLevel level, string message, Exception? exception = null)
        {
            if (level == LogLevel.Error)
            {
                Errors.Writer.TryWrite(message);
            }
        }
    }
}
