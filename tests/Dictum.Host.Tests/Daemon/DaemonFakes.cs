using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Host.Daemon;
using Dictum.Platform;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Injection;
using Dictum.Testing;

namespace Dictum.Host.Tests.Daemon;

internal sealed class EchoStt(string text) : ISpeechToText
{
    public string Id => "echo";

    public TaskCompletionSource? Gate { get; set; }

    public string Text { get; set; } = text;

    public Exception? FailWith { get; set; }

    /// <summary>The token of the last transcription: the dictation's own.</summary>
    public CancellationToken LastToken { get; private set; }

    public async Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct)
    {
        LastToken = ct;
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        if (FailWith is not null)
        {
            throw FailWith;
        }

        return new Transcript(Text, request.Language, TimeSpan.Zero);
    }

    public Task<HealthStatus> CheckAsync(CancellationToken ct) => Task.FromResult(HealthStatus.Ok("echo"));
}

internal sealed class FakeEngine : IDictationEngine
{
    private readonly EchoStt _stt;

    public FakeEngine(DictumConfig config, EchoStt stt)
    {
        Config = config;
        _stt = stt;
        Pipeline = new DictationPipeline(Audio, null, NullLog.Instance);
    }

    public DictumConfig Config { get; }

    public FakeAudioSource Audio { get; } = new();

    IAudioSource IDictationEngine.Audio => Audio;

    public DictationPipeline Pipeline { get; }

    public bool Disposed { get; private set; }

    public DictationProfile ResolveProfile(string profileId) =>
        new(profileId, _stt, SttRequest.ForSetting(Config.Profiles.First(p => p.Id == profileId).Language, null)!, []);

    /// <summary>What the warm-up waits for: done at once unless a test holds it, or fails it.</summary>
    public Task WarmingUp { get; init; } = Task.CompletedTask;

    /// <summary>The token the warm-up got: the daemon's stopping token.</summary>
    public CancellationToken WarmUpToken { get; private set; }

    public Task WarmUpAsync(CancellationToken ct)
    {
        WarmUpToken = ct;
        return WarmingUp.WaitAsync(ct);
    }

    public ITextProcessor GetProcessor(string id) => throw new ConfigException($"Unknown processor '{id}'.");

    public ISpeechToText GetStt(string id) =>
        Config.Stt.ContainsKey(id) ? _stt : throw new ConfigException($"Unknown STT provider '{id}'.");

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeHotkeys : IHotkeyService
{
    private TaskCompletionSource? _hold;

    public event Action<HotkeyEvent>? Triggered;

    public IReadOnlyList<HotkeyBinding> Bindings { get; private set; } = [];

    public Exception? FailWith { get; set; }

    public HotkeyCapabilities Capabilities { get; } = new(true, false, false, "fake hotkeys");

    /// <summary>Completes when a registration held by <see cref="HoldNext"/> has started and waits.</summary>
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The next registration waits, as a slow portal does, until the returned source completes.</summary>
    public TaskCompletionSource HoldNext()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hold = hold;
        return hold;
    }

    public async Task SetBindingsAsync(IReadOnlyList<HotkeyBinding> bindings, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        if (Interlocked.Exchange(ref _hold, null) is { } hold)
        {
            Held.TrySetResult();
            await hold.Task.WaitAsync(ct);
        }

        Bindings = bindings;
    }

    public void Raise(string id, HotkeyPhase phase) => Triggered?.Invoke(new HotkeyEvent(id, phase));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeInjector : ITextInjector
{
    public List<(string Text, InjectionOptions Options)> Injected { get; } = [];

    public Exception? FailWith { get; set; }

    public InjectionCapabilities Capabilities { get; } = new(true, false, true, "fake injector");

    public Task InjectAsync(string text, InjectionOptions options, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Injected.Add((text, options));
        return Task.CompletedTask;
    }
}

internal sealed class FakeNotifier : INotifier
{
    public List<string> Shown { get; } = [];

    public Task NotifyAsync(string summary, string body, NotificationKind kind, CancellationToken ct)
    {
        Shown.Add($"{summary}: {body}");
        return Task.CompletedTask;
    }
}
