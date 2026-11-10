using Dictum.Core.Configuration;

namespace Dictum.Plugins;

/// <summary>
/// A ready plugin: where it listens, the key every request carries, and what it said about itself. It prints without
/// the key, wherever it is formatted.
/// </summary>
public sealed record PluginEndpoint(Uri Url, string Key, PluginInfo Info)
{
    public override string ToString() => KeyRedaction.Print(nameof(PluginEndpoint), (this with { Key = KeyRedaction.Mask }).PrintMembers);
}

/// <summary>A plugin's answer to <c>GET /dictum/v1/info</c>; <see cref="Model"/> is null when it runs without a model.</summary>
public sealed record PluginInfo(int Protocol, string? Name, string? Version, PluginModelInfo? Model);

public sealed record PluginModelInfo(string? Id, IReadOnlyList<string>? Languages, bool SetLanguage = true, bool Prompt = true);
