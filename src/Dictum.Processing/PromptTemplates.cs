using System.Collections.Concurrent;
using System.Globalization;
using Dictum.Core.Configuration;
using Dictum.Core.Processing;

namespace Dictum.Processing;

/// <summary>
/// LLM prompt templates: <c>&lt;config&gt;/prompts/&lt;name&gt;.md</c> overrides the built-in
/// <c>cleanup</c>, <c>email</c> and <c>code-comment</c>. Placeholders: <c>{{text}}</c>, <c>{{language}}</c>, <c>{{app}}</c>.
/// </summary>
public sealed class PromptTemplates(string userDirectory)
{
    private const string ResourcePrefix = "Dictum.Processing.Prompts.";

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> BuiltIn { get; } =
        [.. typeof(PromptTemplates).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^".md".Length])
            .Order(StringComparer.Ordinal)];

    public string Load(string name) => _cache.GetOrAdd(name, LoadUncached);

    public string Render(string name, string text, ProcessingContext context) =>
        Load(name)
            .Replace("{{text}}", text, StringComparison.Ordinal)
            .Replace("{{language}}", DescribeLanguage(context.Language), StringComparison.Ordinal)
            .Replace("{{app}}", context.ActiveApp ?? "unknown", StringComparison.Ordinal);

    /// <summary>"Ukrainian (uk)" for a known code, otherwise a neutral phrase.</summary>
    public static string DescribeLanguage(string? code)
    {
        if (code is null or "auto")
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
