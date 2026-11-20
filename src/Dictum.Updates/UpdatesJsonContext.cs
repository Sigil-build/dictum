using System.Text.Json.Serialization;

namespace Dictum.Updates;

/// <summary>Every JSON type of Dictum.Updates, read and written without reflection.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(PendingUpdate))]
internal sealed partial class UpdatesJsonContext : JsonSerializerContext;
