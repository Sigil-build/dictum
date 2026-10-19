using System.Text.Json.Serialization;
using Dictum.Core.Audio;
using Dictum.Core.Pipeline;
using Dictum.Platform;

namespace Dictum.Host.Ipc;

public static class IpcProtocol
{
    /// <summary>Bumped whenever a message changes shape; the desktop app refuses a daemon with another version.</summary>
    public const int Version = 1;

    /// <summary>Every message is one line of JSON.</summary>
    internal const char LineEnd = '\n';
}

/// <summary>The commands the daemon answers, sent as <see cref="IpcRequest.Command"/>.</summary>
public static class IpcCommand
{
    public const string Toggle = "toggle";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Cancel = "cancel";
    public const string Status = "status";
    public const string Reload = "reload";
    public const string Subscribe = "subscribe";
    public const string Shutdown = "shutdown";
    public const string PauseHotkeys = "pause-hotkeys";
    public const string ResumeHotkeys = "resume-hotkeys";
    public const string UseProfile = "use-profile";
    public const string Devices = "devices";
    public const string Doctor = "doctor";
    public const string TestStt = "test-stt";
    public const string MonitorStart = "monitor-start";
    public const string MonitorStop = "monitor-stop";
}

/// <summary>The kinds of <see cref="IpcEvent"/> on the <c>subscribe</c> stream.</summary>
public static class IpcEventKind
{
    public const string State = "state";
    public const string Frame = "frame";
    public const string Finished = "finished";
    public const string Status = "status";
    public const string Stopping = "stopping";
}

/// <summary>Why the daemon sent a <c>stopping</c> event.</summary>
public static class StoppingReason
{
    /// <summary>A client sent <c>shutdown</c>.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>The process ends for another reason, such as Ctrl+C or SIGTERM.</summary>
    public const string Exit = "exit";
}

/// <param name="Command">One of <see cref="IpcCommand"/>.</param>
/// <param name="Profile">Profile id; null means the selected or first profile.</param>
/// <param name="Stt">STT provider id, for <c>test-stt</c>.</param>
/// <param name="Device">Microphone, for <c>monitor-start</c>; null means the configured one.</param>
/// <param name="Seconds">Recording length for <c>test-stt</c>.</param>
/// <param name="Sample">For <c>test-stt</c>: transcribe the built-in sample instead of recording.</param>
public sealed record IpcRequest(string Command, string? Profile = null, string? Stt = null, string? Device = null, int? Seconds = null, bool? Sample = null);

public sealed record IpcResponse(
    bool Ok,
    string? Error = null,
    DaemonStatus? Status = null,
    IReadOnlyList<AudioDevice>? Devices = null,
    IReadOnlyList<PlatformCheck>? Checks = null,
    SttTestResult? Test = null)
{
    public static IpcResponse Success(DaemonStatus status) => new(true, Status: status);

    public static IpcResponse Failure(string error) => new(false, error);
}

/// <param name="State">Pipeline state: Idle, Recording, Transcribing, Processing or Output.</param>
/// <param name="ActiveProfile">The profile being dictated with right now.</param>
/// <param name="SelectedProfile">The profile chosen with <c>use-profile</c>, used when a command names none.</param>
/// <param name="WarmedUp">The current speech models finished their warm-up (loaded, or failed and logged); false while
/// they load after a start or a reload; null from a daemon that does not say.</param>
public sealed record DaemonStatus(
    string State,
    string? ActiveProfile,
    IReadOnlyList<ProfileStatus> Profiles,
    string HotkeyBackend,
    string InjectorBackend,
    int ProcessId,
    string? Warning = null,
    bool Paused = false,
    string? SelectedProfile = null,
    IReadOnlyList<string>? ConfigWarnings = null,
    int Protocol = IpcProtocol.Version,
    bool? WarmedUp = null);

/// <param name="Hotkey">Canonical hotkey, or null when the profile has none.</param>
/// <param name="Registered">The hotkey backend accepted the binding.</param>
public sealed record ProfileStatus(string Id, string? Hotkey, string Mode, bool Registered);

/// <param name="AudioSeconds">Length of the transcribed audio, so a client can relate the latency to it.</param>
public sealed record SttTestResult(string Text, string? Language, long LatencyMs, string? Backend, string? Model, double? AudioSeconds = null);

public static class DictationOutcome
{
    public const string Pasted = "pasted";
    public const string Copied = "copied";
    public const string Empty = "empty";
    public const string Cancelled = "cancelled";
    public const string Error = "error";
}

/// <summary>
/// One line of the <c>subscribe</c> stream, of a kind from <see cref="IpcEventKind"/>. Fields that do not apply are left
/// out of the JSON. A frame can arrive just after the state it belonged to has ended; consumers ignore frames outside
/// recording and monitoring.
/// </summary>
public sealed record IpcEvent(
    string Event,
    string? State = null,
    string? Profile = null,
    string? Mode = null,
    double? T = null,
    float? Rms = null,
    float[]? Bands = null,
    string? Outcome = null,
    string? Message = null,
    string? Language = null,
    long? DurationMs = null,
    long? TranscribeMs = null,
    DaemonStatus? Status = null,
    string? Reason = null)
{
    public static IpcEvent ForState(PipelineState state, string? profile, string? mode) =>
        new(IpcEventKind.State, State: state.ToString(), Profile: profile, Mode: mode);

    public static IpcEvent ForFrame(AudioFrame frame) =>
        new(IpcEventKind.Frame, T: frame.AtSeconds, Rms: frame.Rms, Bands: frame.Bands.ToArray());

    public static IpcEvent Finished(string outcome, string? message, string? language, long durationMs, long? transcribeMs) =>
        new(IpcEventKind.Finished, Outcome: outcome, Message: message, Language: language, DurationMs: durationMs, TranscribeMs: transcribeMs);

    public static IpcEvent ForStatus(DaemonStatus status) => new(IpcEventKind.Status, Status: status);

    public static IpcEvent Stopping(string reason) => new(IpcEventKind.Stopping, Reason: reason);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(IpcEvent))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
