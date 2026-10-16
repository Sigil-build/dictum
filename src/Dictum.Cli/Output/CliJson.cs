using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.Cli.Output;

/// <summary>Stable <c>--json</c> schema of a transcription.</summary>
internal sealed record TranscriptionJson(
    string Text,
    string RawText,
    string? Language,
    string Profile,
    string Provider,
    string? Backend,
    long AudioMs,
    long TotalMs,
    IReadOnlyList<StageJson> Stages,
    bool Skipped)
{
    public static TranscriptionJson From(DictationResult result) => new(
        result.Text,
        result.RawText,
        result.Language,
        result.ProfileId,
        result.Provider,
        result.Backend,
        (long)result.AudioDuration.TotalMilliseconds,
        (long)result.Stages.Where(s => s.Stage != "record").Sum(s => s.Elapsed.TotalMilliseconds),
        [.. result.Stages.Select(s => new StageJson(s.Stage, (long)s.Elapsed.TotalMilliseconds))],
        result.Skipped);
}

internal sealed record StageJson(string Stage, long Ms);

internal sealed record ProcessJson(string Text, string Input, IReadOnlyList<StageJson> Stages);

internal sealed record DeviceJson(string Id, string Name, string HostApi, int Channels, double SampleRate, bool IsDefault);

internal sealed record ModelJson(string Id, string Kind, string Title, string File, long Size, bool Installed, bool InCatalog);

internal sealed record InitJson(string Config, string SpeechModel, IReadOnlyList<string> Downloaded, string Cleanup, bool? Autostart);

internal sealed record CheckJson(string Name, HealthLevel Level, string Detail, string? Hint);

internal sealed record DoctorJson(string Version, string Os, string Rid, IReadOnlyList<CheckJson> Checks);

internal sealed record FrameJson(double T, float Rms, float[] Bands);

internal sealed record FrameDumpJson(int FramesPerSecond, int Bands, IReadOnlyList<FrameJson> Frames);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TranscriptionJson))]
[JsonSerializable(typeof(ProcessJson))]
[JsonSerializable(typeof(List<DeviceJson>))]
[JsonSerializable(typeof(DoctorJson))]
[JsonSerializable(typeof(List<ModelJson>))]
[JsonSerializable(typeof(InitJson))]
[JsonSerializable(typeof(FrameDumpJson))]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(DaemonStatus))]
[JsonSerializable(typeof(IpcEvent))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
    /// <summary>Keeps non-ASCII text (e.g. Cyrillic) readable instead of \u-escaped.</summary>
    public static CliJsonContext Readable { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter<HealthLevel>(JsonNamingPolicy.CamelCase) },
    });
}
