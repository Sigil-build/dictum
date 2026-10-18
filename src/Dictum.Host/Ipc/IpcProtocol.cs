using System.Text.Json.Serialization;
using Dictum.Core.Audio;
using Dictum.Core.Pipeline;
using Dictum.Platform;

namespace Dictum.Host.Ipc;

public static class IpcProtocol
{
    /// <summary>Bumped whenever a message changes shape; the desktop app refuses a daemon with another version.</summary>
    public const int Version = 1;
}

/// <param name="Command">
/// <c>toggle</c>, <c>start</c>, <c>stop</c>, <c>cancel</c>, <c>status</c>, <c>reload</c>, <c>subscribe</c>, <c>shutdown</c>,
/// <c>pause-hotkeys</c>, <c>resume-hotkeys</c>, <c>use-profile</c>, <c>devices</c>, <c>monitor-start</c>, <c>monitor-stop</c>,
/// <c>test-stt</c> or <c>doctor</c>.
/// </param>
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

/// <param name="AudioSeconds">How long the transcribed audio was; Settings → Models divides the latency by it ("Measure").</param>
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
/// One line of the <c>subscribe</c> stream: <c>state</c>, <c>frame</c>, <c>finished</c>, <c>status</c> or <c>stopping</c>.
/// Fields that do not apply are left out of the JSON. A frame can arrive just after the state it belonged to has
/// ended; consumers ignore frames outside recording and monitoring.
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
        new("state", State: state.ToString(), Profile: profile, Mode: mode);

    public static IpcEvent ForFrame(AudioFrame frame) =>
        new("frame", T: Math.Round(frame.At.TotalSeconds, 3), Rms: frame.Rms, Bands: frame.Bands.ToArray());

    public static IpcEvent Finished(string outcome, string? message, string? language, long durationMs, long? transcribeMs) =>
        new("finished", Outcome: outcome, Message: message, Language: language, DurationMs: durationMs, TranscribeMs: transcribeMs);

    public static IpcEvent ForStatus(DaemonStatus status) => new("status", Status: status);

    public static IpcEvent Stopping(string reason) => new("stopping", Reason: reason);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(IpcEvent))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
