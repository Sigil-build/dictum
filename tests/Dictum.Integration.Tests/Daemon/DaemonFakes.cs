using Dictum.Audio.PortAudio;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Core.Stt;
using Dictum.Host.Daemon;
using Dictum.Platform;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Injection;

namespace Dictum.Integration.Tests.Daemon;

internal sealed class FakeAudio : IAudioSource
{
    public event Action<AudioFrame>? FrameCaptured;

    public IReadOnlyList<AudioDevice> ListDevices() => [];

    public Task StartAsync(string? deviceId, CancellationToken ct)
    {
        FrameCaptured?.Invoke(default);
        return Task.CompletedTask;
    }

    public Task<AudioClip> StopAsync(CancellationToken ct) => Task.FromResult(new AudioClip(new float[AudioClip.WhisperSampleRate]));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class EchoStt(string text) : ISpeechToText
{
    public string Id => "echo";

    public SttCapabilities Capabilities { get; } = new(true, true, false);

    public TaskCompletionSource? Gate { get; set; }

    public async Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct)
    {
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        return new Transcript(text, request.Language, TimeSpan.Zero);
    }

    public Task<HealthStatus> CheckAsync(CancellationToken ct) => Task.FromResult(HealthStatus.Ok("echo"));
}

internal sealed class FakeEngine(DictumConfig config, EchoStt stt) : IDictationEngine
{
    public DictumConfig Config { get; } = config;

    public DictationPipeline Pipeline { get; } = new(new FakeAudio(), null, NullLog.Instance);

    public bool Disposed { get; private set; }

    public DictationProfile ResolveProfile(string profileId) =>
        new(profileId, stt, new SttRequest(Config.Profiles.First(p => p.Id == profileId).Language, null), []);

    public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeHotkeys : IHotkeyService
{
    public event Action<HotkeyEvent>? Triggered;

    public IReadOnlyList<HotkeyBinding> Bindings { get; private set; } = [];

    public Exception? FailWith { get; set; }

    public HotkeyCapabilities Capabilities { get; } = new(true, false, false, "fake hotkeys");

    public Task SetBindingsAsync(IReadOnlyList<HotkeyBinding> bindings, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Bindings = bindings;
        return Task.CompletedTask;
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

internal sealed class FakeSounds : ISoundPlayer
{
    public List<SoundCue> Played { get; } = [];

    public Task PlayAsync(SoundCue cue, CancellationToken ct)
    {
        lock (Played)
        {
            Played.Add(cue);
        }

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
