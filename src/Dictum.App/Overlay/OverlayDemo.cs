using Avalonia.Threading;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Overlay;

/// <summary>The arguments of <c>dictum-gui --overlay-demo &lt;frames.json&gt; [--state &lt;name&gt;]</c>.</summary>
public sealed record OverlayDemoOptions(string Path, string State);

/// <summary>
/// <c>--overlay-demo</c>: plays a recording through the real overlay window, then the rest of a dictation on fixed demo
/// timings (Transcribing, Cleaning up, Done), and exits. <c>--state</c> starts at a later state instead. The events go
/// through the same <see cref="OverlayController"/> the daemon's events do.
/// </summary>
public sealed class OverlayDemo
{
    /// <summary>The command line flag that runs the demo.</summary>
    public const string Flag = "--overlay-demo";

    private const string StateFlag = "--state";

    private const string ListeningState = "listening";
    private const string SilenceState = "silence";
    private const string TranscribingState = "transcribing";
    private const string CleaningState = "cleaning";
    private const string DoneState = "done";
    private const string CancelledState = "cancelled";
    private const string ErrorState = "error";

    public static readonly IReadOnlyList<string> States =
        [ListeningState, SilenceState, TranscribingState, CleaningState, DoneState, CancelledState, ErrorState];

    private const string Profile = "demo";
    private const string Stt = "demo";
    private const string Languages = "uk, en";
    private const string PickedLanguage = "uk";
    private const string DemoError = "Microphone not found";

    // The script's timings, in seconds.
    private const double SilenceFor = 3;
    private const double TranscribingFor = 1.6;
    private const double CleaningFor = 0.9;
    private const double DoneAt = TranscribingFor + CleaningFor;

    /// <summary>Seconds after the last event before the demo gives up on the overlay going away.</summary>
    private const double GiveUpAfter = 10;

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
        var demo = IndexOf(args, Flag);
        if (demo < 0 || demo + 1 >= args.Count || args[demo + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine($"Usage: dictum-gui {Flag} <frames.json> [{StateFlag} <{string.Join("|", States)}>]");
            return null;
        }

        var state = ListeningState;
        var named = IndexOf(args, StateFlag);
        if (named >= 0)
        {
            if (named + 1 >= args.Count || !States.Contains(args[named + 1]))
            {
                error.WriteLine($"{StateFlag} takes one of: {string.Join(", ", States)}");
                return null;
            }

            state = args[named + 1];
        }

        return new OverlayDemoOptions(args[demo + 1], state);
    }

    /// <summary>What the demo shows: the configured overlay look, always on, and a profile that lists two languages.</summary>
    public static DictumConfig Config(DictumConfig? user) => new()
    {
        General = new GeneralSettings { Overlay = (user?.General.Overlay ?? new OverlaySettings()) with { Enabled = true } },
        Profiles = [new ProfileConfig { Id = Profile, Stt = Stt, Language = Languages }],
    };

    /// <summary>Starts the demo's clock; <see cref="Start"/> also runs the timer that drives <see cref="Step"/>.</summary>
    public void Begin() => _start = _time.GetUtcNow();

    /// <summary>
    /// On the UI thread: starts the clock and a timer that calls <paramref name="exit"/> with 0 once the overlay is gone
    /// (1 if it never goes).
    /// </summary>
    public void Start(Action<int> exit)
    {
        Begin();
        var deadline = _start + TimeSpan.FromSeconds(_script[^1].At + GiveUpAfter);
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
            _controller.OnFrame(new IpcEvent(IpcEventKind.Frame, T: frame.T, Rms: frame.Rms, Bands: frame.Bands));
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
        static IpcEvent State(PipelineState state) => IpcEvent.ForState(state, Profile, nameof(HotkeyMode.Toggle));
        static IpcEvent Finished(string outcome, string? message = null) => new(IpcEventKind.Finished, Outcome: outcome, Message: message, Language: PickedLanguage);

        var events = new List<(double, IpcEvent)> { (0, State(PipelineState.Recording)) };
        var listen = state switch { ListeningState => duration, SilenceState => SilenceFor, _ => 0 };
        switch (state)
        {
            case ListeningState or SilenceState or TranscribingState:
                events.Add((listen, State(PipelineState.Transcribing)));
                events.Add((listen + TranscribingFor, State(PipelineState.Processing)));
                events.Add((listen + DoneAt, State(PipelineState.Idle)));
                events.Add((listen + DoneAt, Finished(DictationOutcome.Pasted)));
                break;
            case CleaningState:
                events.Add((0, State(PipelineState.Processing)));
                events.Add((CleaningFor, State(PipelineState.Idle)));
                events.Add((CleaningFor, Finished(DictationOutcome.Pasted)));
                break;
            case DoneState:
                events.Add((0, State(PipelineState.Idle)));
                events.Add((0, Finished(DictationOutcome.Pasted)));
                break;
            case CancelledState:
                events.Add((0, State(PipelineState.Idle)));
                events.Add((0, Finished(DictationOutcome.Cancelled)));
                break;
            default:
                events.Add((0, State(PipelineState.Idle)));
                events.Add((0, Finished(DictationOutcome.Error, DemoError)));
                break;
        }

        return (events, state == ListeningState ? duration : 0);
    }
}
