using System.Text;

namespace Dictum.Plugins;

/// <summary>
/// What the log carries with the warning of a plugin that could not start, so that a command that reports the same
/// failure itself can keep the warning off its console (<see cref="Dictum.Core.Diagnostics.DictumLog.ConsoleFilter"/>).
/// A failure Dictum expects, such as an unconfirmed program, shows its hint and the plugin's last lines where an
/// exception would show its stack trace; an unexpected one shows the exception it wraps, stack trace and all.
/// </summary>
public sealed class PluginStartFailedException : Exception
{
    private readonly string? _details;

    public PluginStartFailedException(PluginUnavailableException expected)
        : base(expected.Message)
    {
        _details = Details(expected);
    }

    public PluginStartFailedException(string message, Exception unexpected)
        : base(message, unexpected)
    {
    }

    /// <summary>The hint and the plugin's last lines, each spelt out as in any text from the plugin; null when there are none.</summary>
    internal static string? Details(PluginUnavailableException ex)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrEmpty(ex.Hint))
        {
            text.Append("Hint: ").Append(PluginText.Show(ex.Hint));
        }

        if (ex.Output.Count > 0)
        {
            text.Append(text.Length == 0 ? "Last output:" : $"{Environment.NewLine}Last output:");
            foreach (var line in ex.Output)
            {
                text.AppendLine().Append("  ").Append(PluginText.Show(line));
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }

    public override string ToString() => _details ?? (InnerException is null ? Message : base.ToString());
}
