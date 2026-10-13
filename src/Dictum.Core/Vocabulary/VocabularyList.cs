namespace Dictum.Core.Vocabulary;

/// <summary>
/// A profile's words, names and terms the recognizer should spell exactly. Earlier terms win when the
/// speech hint runs out of room, so the order is kept everywhere.
/// </summary>
public static class VocabularyList
{
    /// <summary>Trimmed, without empty entries or case-insensitive duplicates, in the original order.</summary>
    public static List<string> Normalize(IEnumerable<string> terms) => Merge([], terms);

    public static List<string> Merge(IEnumerable<string> existing, IEnumerable<string> added)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in existing.Concat(added))
        {
            var trimmed = term.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    public static (List<string> Remaining, List<string> NotFound) Remove(IEnumerable<string> existing, IEnumerable<string> removed)
    {
        var wanted = new HashSet<string>(removed.Select(t => t.Trim()).Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<string>();
        foreach (var term in existing)
        {
            if (wanted.Contains(term.Trim()))
            {
                found.Add(term.Trim());
            }
            else
            {
                remaining.Add(term);
            }
        }

        return (remaining, [.. wanted.Where(t => !found.Contains(t))]);
    }

    /// <summary>"Notepad++, VS Code", or null for an empty list.</summary>
    public static string? Render(IReadOnlyList<string>? terms) =>
        terms is { Count: > 0 } ? string.Join(", ", terms) : null;

    /// <summary>One term per line; blank lines and lines starting with '#' are skipped.</summary>
    public static IEnumerable<string> ParseLines(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'));
}
