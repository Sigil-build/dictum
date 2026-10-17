using System.Text.RegularExpressions;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;

namespace Dictum.Processing;

/// <summary>Applies literal and regex replacement rules in order, e.g. a dictionary of technical terms.</summary>
public sealed class ReplaceProcessor : ITextProcessor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<Func<string, string>> _rules;
    private readonly ILog _log;

    public ReplaceProcessor(string id, ReplaceProcessorConfig config, ILog log)
    {
        Id = id;
        _log = log;
        _rules = [.. config.Rules.Select(Compile).OfType<Func<string, string>>()];
    }

    public string Id { get; }

    public Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct)
    {
        foreach (var rule in _rules)
        {
            try
            {
                text = rule(text);
            }
            catch (RegexMatchTimeoutException ex)
            {
                _log.Warn($"Processor '{Id}': rule '{ex.Pattern}' timed out and was skipped");
            }
        }

        return Task.FromResult(text);
    }

    private static Func<string, string>? Compile(ReplaceRule rule)
    {
        if (rule.Disabled)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(rule.Pattern))
        {
            var regex = new Regex(rule.Pattern, RegexOptions.CultureInvariant, RegexTimeout);
            return text => regex.Replace(text, rule.To);
        }

        var from = rule.From ?? string.Empty;
        if (from.Length == 0)
        {
            return null;
        }

        if (rule.WholeWord)
        {
            // No letter or digit on either side; the replacement is literal, so a "$" stays a dollar sign.
            var options = RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var word = new Regex($@"(?<!\w){Regex.Escape(from)}(?!\w)", options, RegexTimeout);
            var to = rule.To.Replace("$", "$$", StringComparison.Ordinal);
            return text => word.Replace(text, to);
        }

        var comparison = rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return text => text.Replace(from, rule.To, comparison);
    }
}
