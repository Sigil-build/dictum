using Dictum.App.Daemon;
using Dictum.Audio.PortAudio;
using Dictum.Core.Audio;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.Tests.Settings;

/// <summary>The daemon as a page sees it: a link and canned answers, recorded.</summary>
internal sealed class FakeChannel : IDaemonChannel
{
    public static readonly AudioDevice HyperX = new("3", "HyperX SoloCast", "WASAPI", 1, 48000, true);
    public static readonly AudioDevice Webcam = new("5", "Webcam C920", "WASAPI", 2, 48000, false);

    public DaemonLink Link { get; set; } = Connected();

    public List<IpcRequest> Sent { get; } = [];

    public TimeSpan? LastTimeout { get; private set; }

    /// <summary>Answers the test gives later, by command; each is used once.</summary>
    public Dictionary<string, TaskCompletionSource<IpcResponse>> Held { get; } = [];

    /// <summary>Thrown by every send: a broken answer or connection.</summary>
    public Exception? Throws { get; set; }

    public IEnumerable<string> Commands => Sent.Select(r => r.Command);

    public Func<IpcRequest, IpcResponse> Answer { get; set; } = request => request.Command switch
    {
        "devices" => new IpcResponse(true, Devices: [HyperX, Webcam]),
        _ => new IpcResponse(true),
    };

    public static DaemonLink Connected() =>
        new(DaemonLinkState.Connected, new DaemonStatus("Idle", null, [], "hook", "paste", 4242));

    public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? timeout = null)
    {
        Sent.Add(request);
        LastTimeout = timeout;
        if (Throws is not null)
        {
            return Task.FromException<IpcResponse>(Throws);
        }

        if (Held.Remove(request.Command, out var held))
        {
            return held.Task;
        }

        return Link.State == DaemonLinkState.Connected
            ? Task.FromResult(Answer(request))
            : Task.FromException<IpcResponse>(new DaemonNotRunningException("The daemon is not running."));
    }
}

internal sealed class FakeAutostart : IAutostart
{
    public LoginApp? Installed { get; set; }

    public LoginItem? Item { get; private set; }

    public Exception? Fails { get; set; }

    public string Location(LoginApp app) => "fake";

    public Task InstallAsync(LoginItem item, CancellationToken ct)
    {
        if (Fails is not null)
        {
            return Task.FromException(Fails);
        }

        Item = item;
        Installed = item.App;
        return Task.CompletedTask;
    }

    public Task UninstallAsync(CancellationToken ct)
    {
        if (Fails is not null)
        {
            return Task.FromException(Fails);
        }

        Installed = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeSounds : ISoundPlayer
{
    public List<SoundCue> Played { get; } = [];

    public Exception? Fails { get; set; }

    public Task PlayAsync(SoundCue cue, CancellationToken ct)
    {
        if (Fails is not null)
        {
            return Task.FromException(Fails);
        }

        Played.Add(cue);
        return Task.CompletedTask;
    }
}
