using Dictum.Core.Diagnostics;
using Dictum.Core.Text;

namespace Dictum.Plugins;

/// <summary>A plugin that could not start or be reached; <see cref="Output"/> holds its last lines, which often say why.</summary>
public sealed class PluginUnavailableException(string message, string? hint = null, IReadOnlyList<string>? output = null, Exception? inner = null)
    : Exception(message, inner), IHasHint
{
    /// <summary>How many of the plugin's last lines a one-line report quotes.</summary>
    private const int QuotedLines = 5;

    public string? Hint { get; } = hint;

    public IReadOnlyList<string> Output { get; } = output ?? [];

    /// <summary>
    /// <paramref name="text"/>, such as the message, followed by the plugin's last lines when it printed any: what a
    /// one-line report shows, such as doctor's row or the failure of a provider's Test. The lines are the plugin's own, and
    /// a terminal must not be given them raw, so each is spelt out; the stored lines stay as the plugin printed them.
    /// </summary>
    public string WithOutput(string text) =>
        Output.Count == 0 ? text : $"{(text.EndsWith('.') ? text[..^1] : text)}; last output: {string.Join(" | ", Output.TakeLast(QuotedLines).Select(TerminalText.Show))}";
}
