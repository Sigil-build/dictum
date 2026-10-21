using System.Text.RegularExpressions;

namespace Dictum.Core.Tests.Docs;

/// <summary>
/// Fenced code blocks in the docs' Markdown, as the site's Python-Markdown reads them: a fence opens with three or more
/// <c>`</c> or <c>~</c>, and only a run of the same character at least as long closes it.
/// </summary>
internal static partial class MarkdownFences
{
    /// <summary>The lines outside fenced code blocks: there a <c>#</c> or a <c>](</c> is sample text, not Markdown.</summary>
    public static IEnumerable<string> ProseLines(IEnumerable<string> lines)
    {
        string? fence = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (fence is null && Fence().Match(trimmed) is { Success: true } open)
            {
                fence = open.Value;
            }
            else if (fence is null)
            {
                yield return line;
            }
            else if (trimmed.Length >= fence.Length && trimmed.Trim(fence[0]).Length == 0)
            {
                fence = null;
            }
        }
    }

    /// <summary>A fence at the start of a trimmed line; its value is the run of backticks or tildes.</summary>
    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    public static partial Regex Fence();
}
