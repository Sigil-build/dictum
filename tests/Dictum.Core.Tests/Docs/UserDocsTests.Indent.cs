using System.Text.RegularExpressions;

namespace Dictum.Core.Tests.Docs;

public partial class UserDocsTests
{
    /// <summary>The indent Python-Markdown nests by: a list item's or a tab's content sits this much deeper.</summary>
    private const int NestingIndent = 4;

    // The site's Python-Markdown nests a list item, a fenced block or a tab's content only at four spaces: at two, a
    // nested list renders flat and a fence inside a step ends the list. GitHub nests at two as well, so it hides this.
    [Fact]
    public void ListsAndFencesAreIndentedByFourSpaces()
    {
        var problems = new List<string>();
        foreach (var page in AllPages())
        {
            var lines = Lines(Full(page));
            (string Marker, int Indent, int Line)? fence = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].TrimStart();
                var lead = lines[i][..^text.Length];
                var indent = lead.Length;
                var at = $"{page}:{i + 1}";
                if (fence is { } open)
                {
                    var closing = text.TrimEnd();
                    if (closing.Length >= open.Marker.Length && closing.Trim(open.Marker[0]).Length == 0)
                    {
                        if (indent != open.Indent)
                        {
                            problems.Add($"{at}: the fence opened on line {open.Line} at {open.Indent} spaces closes at {indent}");
                        }

                        fence = null;
                    }

                    continue;
                }

                var block = Fence().Match(text) is { Success: true } start ? start.Value : BlockStart().Match(text).Value;
                if (block.Length == 0)
                {
                    continue;
                }

                if (lead.Contains('\t', StringComparison.Ordinal))
                {
                    problems.Add($"{at}: \"{text}\" is indented with a tab; use spaces");
                }
                else if (indent % NestingIndent != 0)
                {
                    problems.Add($"{at}: \"{text}\" is indented by {indent} spaces, not a multiple of {NestingIndent}");
                }

                if (Fence().IsMatch(text))
                {
                    fence = (block, indent, i + 1);
                }
            }

            if (fence is { } unclosed)
            {
                problems.Add($"{page}:{unclosed.Line}: the fence never closes");
            }
        }

        AssertNone(problems);
    }

    /// <summary>The start of a list item (<c>- </c>, <c>* </c>, <c>+ </c>, <c>1. </c>) or of a tab (<c>=== </c>).</summary>
    [GeneratedRegex(@"^(?:[-*+]|\d+\.|===)(?=\s)")]
    private static partial Regex BlockStart();
}
