namespace Dictum.Core.Vocabulary;

/// <param name="Prompt">Text for <c>SttRequest.Prompt</c>, or null when there is nothing to hint.</param>
/// <param name="Dropped">Vocabulary terms that did not fit <see cref="SttPromptBuilder.MaxLength"/>.</param>
public sealed record SttHint(string? Prompt, IReadOnlyList<string> Dropped);

/// <summary>Turns a profile's <c>sttPrompt</c> and vocabulary into the hint Whisper sees.</summary>
public static class SttPromptBuilder
{
    /// <summary>
    /// Whisper uses only about the last 224 prompt tokens; 400 characters of mixed Cyrillic and Latin
    /// stays under that, so nothing is cut silently by the model.
    /// </summary>
    public const int MaxLength = 400;

    /// <param name="vocabulary">Already normalized (<see cref="VocabularyList.Normalize"/>).</param>
    public static SttHint Build(string? sttPrompt, IReadOnlyList<string> vocabulary)
    {
        var prefix = string.IsNullOrWhiteSpace(sttPrompt) ? null : sttPrompt.Trim();
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var term in vocabulary)
        {
            if (dropped.Count == 0 && Compose(prefix, [.. kept, term]).Length <= MaxLength)
            {
                kept.Add(term);
            }
            else
            {
                dropped.Add(term);
            }
        }

        return new SttHint(kept.Count == 0 ? prefix : Compose(prefix, kept), dropped);
    }

    private static string Compose(string? prefix, IReadOnlyList<string> terms)
    {
        var list = string.Join(", ", terms) + ".";
        return prefix is null ? list : $"{prefix} {list}";
    }
}
