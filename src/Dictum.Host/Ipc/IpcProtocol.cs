using System.Text.Json.Serialization;

namespace Dictum.Host.Ipc;

/// <param name="Command"><c>toggle</c>, <c>start</c>, <c>stop</c>, <c>cancel</c>, <c>status</c> or <c>reload</c>.</param>
/// <param name="Profile">Profile id; null means the active or first profile.</param>
public sealed record IpcRequest(string Command, string? Profile = null);

public sealed record IpcResponse(bool Ok, string? Error = null, DaemonStatus? Status = null)
{
    public static IpcResponse Success(DaemonStatus status) => new(true, Status: status);

    public static IpcResponse Failure(string error) => new(false, error);
}

/// <param name="State">Pipeline state: Idle, Recording, Transcribing, Processing or Output.</param>
public sealed record DaemonStatus(
    string State,
    string? ActiveProfile,
    IReadOnlyList<ProfileStatus> Profiles,
    string HotkeyBackend,
    string InjectorBackend,
    int ProcessId,
    string? Warning = null);

/// <param name="Hotkey">Canonical hotkey, or null when the profile has none.</param>
/// <param name="Registered">The hotkey backend accepted the binding.</param>
public sealed record ProfileStatus(string Id, string? Hotkey, string Mode, bool Registered);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
