using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictum.Plugins;

/// <summary>Unknown fields are ignored (the default), as protocol 1 lets a newer plugin add fields an older Dictum skips.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginTrustFile))]
[JsonSerializable(typeof(PluginTrustVersion))]
[JsonSerializable(typeof(PluginReadyLine))]
[JsonSerializable(typeof(PluginInfo))]
[JsonSerializable(typeof(PluginTranscription))]
internal sealed partial class PluginJsonContext : JsonSerializerContext;
