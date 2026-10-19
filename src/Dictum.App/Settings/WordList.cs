namespace Dictum.App.Settings;

/// <summary>
/// "Your words" and terminal apps as chips. Typed or pasted text splits on newline, comma and semicolon. The
/// part after the last separator is still being typed and stays in the box. Repeats are dropped, ignoring case, as the
/// validator would warn about them.
/// </summary>
public static class WordList
{
    private static readonly char[] Separators = ['\n', '\r', ',', ';'];

    public static (IReadOnlyList<string> Words, string Typing) Split(string text)
    {
        var parts = text.Split(Separators);
        IReadOnlyList<string> words = [.. parts[..^1].Select(p => p.Trim()).Where(p => p.Length > 0)];
        return (words, parts[^1].TrimStart());
    }

    public static List<string> Add(IReadOnlyList<string> list, IEnumerable<string> words)
    {
        var result = list.ToList();
        var seen = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        foreach (var word in words.Select(w => w.Trim()).Where(w => w.Length > 0))
        {
            if (seen.Add(word))
            {
                result.Add(word);
            }
        }

        return result;
    }
}
