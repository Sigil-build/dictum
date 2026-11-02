using Dictum.Core.Diagnostics;

namespace Dictum.Plugins;

/// <summary>A plugin that could not start or be reached; <see cref="Output"/> holds its last lines, which often say why.</summary>
public sealed class PluginUnavailableException(string message, string? hint = null, IReadOnlyList<string>? output = null)
    : Exception(message), IHasHint
{
    public string? Hint { get; } = hint;

    public IReadOnlyList<string> Output { get; } = output ?? [];
}
