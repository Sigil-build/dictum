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
    /// <summary>The <c>LogicalName</c> prefix the project file gives the embedded templates.</summary>
    private const string ResourcePrefix = "Dictum.Processing.Prompts.";

    /// <summary>The extension of a template file, built in or in the user's prompts folder.</summary>
    public const string Extension = ".md";

    private readonly ConcurrentDictionary<string, (Stamp? Stamp, string Text)> _cache = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> BuiltIn { get; } =
        [.. typeof(PromptTemplates).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^Extension.Length])
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The template by name. A user file is read again when its time or size changes, so an edit made in Settings or by
    /// hand reaches the next dictation without a config reload, although the daemon keeps one instance per engine. The
    /// cost is one file stat per load.
    /// </summary>
    public string Load(string name)
    {
        var userFile = UserFile(name);
        var stamp = StampOf(userFile);
        if (_cache.TryGetValue(name, out var cached) && cached.Stamp == stamp)
        {
            return cached.Text;
        }

        var text = stamp is null ? LoadBuiltIn(name, userFile) : File.ReadAllText(userFile);
        _cache[name] = (stamp, text);
        return text;
    }

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
        if (code is null or LanguageList.Auto || code.Contains(','))
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

    private string UserFile(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ConfigException($"Invalid prompt name '{name}'.");
        }

        return FileIn(userDirectory, name);
    }

    /// <summary>Where the prompts folder keeps the template <paramref name="name"/>.</summary>
    internal static string FileIn(string directory, string name) => Path.Combine(directory, name + Extension);

    private static Stamp? StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new Stamp(info.LastWriteTimeUtc, info.Length) : null;
    }

    private static string LoadBuiltIn(string name, string userFile)
    {
        using var stream = typeof(PromptTemplates).Assembly.GetManifestResourceStream(ResourcePrefix + name + Extension);
        if (stream is null)
        {
            throw new ConfigException(
                $"Prompt template '{name}' not found. Create {userFile} or use a built-in one: {string.Join(", ", BuiltIn)}.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private readonly record struct Stamp(DateTime Written, long Length);
}
