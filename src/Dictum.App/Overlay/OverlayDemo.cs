using Avalonia.Threading;
using Dictum.Core.Configuration;
using Dictum.Host.Ipc;

namespace Dictum.App.Overlay;

/// <summary>The arguments of <c>dictum-gui --overlay-demo &lt;frames.json&gt; [--state &lt;name&gt;]</c>.</summary>
public sealed record OverlayDemoOptions(string Path, string State);

/// <summary>
/// <c>--overlay-demo</c> (spec 5.5): plays a recording through the real overlay window, then the rest of a dictation
/// on the prototype's demo timers (Transcribing 1.6 s, Cleaning up 0.9 s, Done), and exits. <c>--state</c> starts at
/// a later state instead. The events go through the same <see cref="OverlayController"/> the daemon's events do.
/// </summary>
public sealed class OverlayDemo
{
    public static readonly IReadOnlyList<string> States = ["listening", "silence", "transcribing", "cleaning", "done", "cancelled", "error"];

    private const string Profile = "demo";
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private readonly OverlayController _controller;
    private readonly FrameRecording _recording;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<(double At, IpcEvent Event)> _script;
    private readonly double _playFor;
    private DateTimeOffset _start;
    private int _sent;

    public OverlayDemo(OverlayController controller, FrameRecording recording, string state, TimeProvider time)
    {
        _controller = controller;
        _recording = recording;
        _time = time;
        (_script, _playFor) = Script(state, recording.Duration.TotalSeconds);
    }

    /// <returns>The options, or null after writing what is wrong to <paramref name="error"/>.</returns>
    public static OverlayDemoOptions? Parse(IReadOnlyList<string> args, TextWriter error)
    {
        var demo = IndexOf(args, "--overlay-demo");
        if (demo < 0 || demo + 1 >= args.Count || args[demo + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine($"Usage: dictum-gui --overlay-demo <frames.json> [--state <{string.Join("|", States)}>]");
            return null;
        }

        var state = "listening";
        var named = IndexOf(args, "--state");
        if (named >= 0)
        {
            if (named + 1 >= args.Count || !States.Contains(args[named + 1]))
            {
                error.WriteLine($"--state takes one of: {string.Join(", ", States)}");
                return null;
            }

            state = args[named + 1];
        }

        return new OverlayDemoOptions(args[demo + 1], state);
    }

    /// <summary>What the demo shows: the user's overlay look, always on, and a profile that lists two languages.</summary>
    public static DictumConfig Config(DictumConfig? user) => new()
    {
        General = new GeneralSettings { Overlay = (user?.General.Overlay ?? new OverlaySettings()) with { Enabled = true } },
        Profiles = [new ProfileConfig { Id = Profile, Stt = "demo", Language = "uk, en" }],
    };

    /// <summary>Starts the demo's clock; <see cref="Start"/> also runs the timer that drives <see cref="Step"/>.</summary>
    public void Begin() => _start = _time.GetUtcNow();

    /// <summary>
    /// On the UI thread: starts the clock and a 20 ms timer that calls <paramref name="exit"/> with 0 once the overlay
    /// is gone (1 if it never goes).
    /// </summary>
    public void Start(Action<int> exit)
    {
        Begin();
        var deadline = _start + TimeSpan.FromSeconds(_script[^1].At + 10);
        var timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += (_, _) =>
        {
            var now = _time.GetUtcNow();
            if (Step(now) || now > deadline)
            {
                timer.Stop();
                exit(now > deadline ? 1 : 0);
            }
        };
        timer.Start();
    }

    /// <summary>Sends what is due at <paramref name="now"/>; true once the whole script was sent and the overlay is gone.</summary>
    public bool Step(DateTimeOffset now)
    {
        var elapsed = (now - _start).TotalSeconds;
        if (elapsed < _playFor && _recording.At(TimeSpan.FromSeconds(elapsed)) is { } frame)
        {
            _controller.OnFrame(new IpcEvent("frame", T: frame.T, Rms: frame.Rms, Bands: frame.Bands));
        }

        while (_sent < _script.Count && _script[_sent].At <= elapsed)
        {
            _controller.OnEvent(_script[_sent].Event);
            _sent++;
        }

        return _sent == _script.Count && _controller.Phase is OverlayPhase.Hidden;
    }

    private static int IndexOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static (IReadOnlyList<(double, IpcEvent)> Script, double PlayFor) Script(string state, double duration)
    {
        static IpcEvent State(string name) => new("state", State: name, Profile: Profile, Mode: "Toggle");
        static IpcEvent Finished(string outcome, string? message = null) => new("finished", Outcome: outcome, Message: message, Language: "uk");

        var events = new List<(double, IpcEvent)> { (0, State("Recording")) };
        var listen = state switch { "listening" => duration, "silence" => 3, _ => 0 };
        switch (state)
        {
            case "listening" or "silence" or "transcribing":
                events.Add((listen, State("Transcribing")));
                events.Add((listen + 1.6, State("Processing")));
                events.Add((listen + 2.5, State("Idle")));
                events.Add((listen + 2.5, Finished(DictationOutcome.Pasted)));
                break;
            case "cleaning":
                events.Add((0, State("Processing")));
                events.Add((0.9, State("Idle")));
                events.Add((0.9, Finished(DictationOutcome.Pasted)));
                break;
            case "done":
                events.Add((0, State("Idle")));
                events.Add((0, Finished(DictationOutcome.Pasted)));
                break;
            case "cancelled":
                events.Add((0, State("Idle")));
                events.Add((0, Finished(DictationOutcome.Cancelled)));
                break;
            default:
                events.Add((0, State("Idle")));
                events.Add((0, Finished(DictationOutcome.Error, "Microphone not found")));
                break;
        }

        return (events, state == "listening" ? duration : 0);
    }
}
