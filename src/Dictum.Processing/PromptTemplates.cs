using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Dictum.Core.Configuration;
using Dictum.Core.Processing;

namespace Dictum.Processing;

/// <summary>
/// LLM prompt templates: <c>&lt;config&gt;/prompts/&lt;name&gt;.md</c> overrides the built-in
/// <c>cleanup</c>, <c>email</c> and <c>code-comment</c>. Placeholders: <c>{{text}}</c>, <c>{{language}}</c>, <c>{{app}}</c>,
/// <c>{{vocabulary}}</c>.
/// </summary>
public sealed partial class PromptTemplates(string userDirectory)
{
    private const string ResourcePrefix = "Dictum.Processing.Prompts.";

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> BuiltIn { get; } =
        [.. typeof(PromptTemplates).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^".md".Length])
            .Order(StringComparer.Ordinal)];

    public string Load(string name) => _cache.GetOrAdd(name, LoadUncached);

    /// <summary>Fills placeholders in one pass, so placeholders inside the dictated text stay as they are.</summary>
    public string Render(string name, string text, ProcessingContext context) =>
        Placeholder().Replace(Load(name), match => match.Groups[1].Value switch
        {
            "text" => text,
            "language" => DescribeLanguage(context.Language),
            "app" => context.ActiveApp ?? "unknown",
            _ => DescribeVocabulary(context.Vocabulary),
        });

    /// <summary>
    /// The sentence <c>{{vocabulary}}</c> becomes; empty when the profile has no word list. Only user templates use it:
    /// with small local models the word list made cleanup worse (appended terms, lost case endings), so the built-in
    /// prompts leave it to the Whisper hint.
    /// </summary>
    public static string DescribeVocabulary(string? vocabulary) =>
        string.IsNullOrWhiteSpace(vocabulary)
            ? string.Empty
            : "The speaker often uses these words and names. If a word in the text looks misheard and one of them fits, "
              + $"use its exact spelling, but never add a word that was not said: {vocabulary}.";

    [GeneratedRegex(@"\{\{(text|language|app|vocabulary)\}\}")]
    private static partial Regex Placeholder();

    /// <summary>"Ukrainian (uk)" for a known code, otherwise a neutral phrase.</summary>
    public static string DescribeLanguage(string? code)
    {
        // A profile's list ("uk, en") says which languages may come, not which one this text is in.
        if (code is null or "auto" || code.Contains(','))
        {
            return "the same language as the text";
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return culture.EnglishName == code ? code : $"{culture.EnglishName} ({code})";
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private string LoadUncached(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ConfigException($"Invalid prompt name '{name}'.");
        }

        var userFile = Path.Combine(userDirectory, name + ".md");
        if (File.Exists(userFile))
        {
            return File.ReadAllText(userFile);
        }

        using var stream = typeof(PromptTemplates).Assembly.GetManifestResourceStream(ResourcePrefix + name + ".md");
        if (stream is null)
        {
            throw new ConfigException(
                $"Prompt template '{name}' not found. Create {userFile} or use a built-in one: {string.Join(", ", BuiltIn)}.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
