using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dictum.Testing;

namespace Dictum.Core.Tests.Docs;

/// <summary>
/// The shape of the user documentation in <c>docs/</c>, which docs.sigil.build publishes in English and Ukrainian:
/// twins with the same heading ids, so a link to a section works in both languages; links that stay inside
/// <c>docs/</c>, as the site only has that folder; and a <c>site.yml</c> that lists every page and names this version.
/// </summary>
public sealed partial class UserDocsTests
{
    private const string UkrainianSuffix = ".uk.md";

    /// <summary>Pages with no Ukrainian twin: the CLI reference is generated from the program's help, which is English.</summary>
    private static readonly string[] EnglishOnly = ["reference/cli.md"];

    private static string Docs => Path.Combine(Repository.Root, "docs");

    private static string SiteFile => Path.Combine(Docs, "site.yml");

    // The app and dictum doctor link to sections by id, in the UI's language, so the ids are the same in both
    // languages and do not come from the heading's text.
    [Fact]
    public void EveryPageHasAUkrainianTwinWithTheSameSections()
    {
        var problems = new List<string>();
        foreach (var page in EnglishPages().Except(EnglishOnly))
        {
            var twin = page[..^".md".Length] + UkrainianSuffix;
            if (!File.Exists(Full(twin)))
            {
                problems.Add($"{page} has no Ukrainian twin {twin}");
                continue;
            }

            problems.AddRange(HeadingProblems(page));
            problems.AddRange(HeadingProblems(twin));
            var (en, uk) = (Ids(page), Ids(twin));
            if (!en.SequenceEqual(uk))
            {
                problems.Add($"{twin} has the ids [{string.Join(", ", uk)}], but {page} has [{string.Join(", ", en)}]");
            }
        }

        foreach (var twin in AllPages().Where(IsUkrainian))
        {
            var page = twin[..^UkrainianSuffix.Length] + ".md";
            if (!File.Exists(Full(page)))
            {
                problems.Add($"{twin} has no English page {page}");
            }
        }

        AssertNone(problems);
    }

    // Links keep `.md` and stay relative, so the pages read the same on GitHub and on the site, which has only docs/.
    // The nav's entries are links as well: one to a missing page fails the site's strict build.
    [Fact(Skip = "until A2–A5")]
    public void EveryRelativeLinkStaysInsideDocsAndResolves()
    {
        var files = Directory.EnumerateFiles(Docs, "*", SearchOption.AllDirectories).Select(Relative).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var page in AllPages())
        {
            foreach (var target in Links(page).Where(target => !IsExternal(target)))
            {
                if (LinkProblem(page, target, files) is { } problem)
                {
                    problems.Add($"{page}: ({target}) {problem}");
                }
            }
        }

        problems.AddRange(NavEntries().Where(entry => !files.Contains(entry)).Select(entry => $"site.yml: the nav's {entry} does not exist"));

        AssertNone(problems);
    }

    // The site prints "This page describes Dictum <version>.", and the docs branch for a release is named after it.
    [Fact]
    public void SiteFileVersionMatchesTheProduct()
    {
        var props = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"));
        var prefix = props.Descendants("VersionPrefix").Single().Value;
        var majorMinor = string.Join('.', prefix.Split('.').Take(2));

        // Quoted, as YAML would read a bare 0.10 as the number 0.1.
        var versions = Lines(SiteFile).Select(line => SiteVersion().Match(line)).Where(match => match.Success).ToList();

        Assert.True(versions.Count == 1, "site.yml needs exactly one line version: \"<major>.<minor>\".");
        Assert.Equal(majorMinor, versions[0].Groups["version"].Value);
    }

    // The site's build warns about a page outside the nav, and its strict build stops on the warning.
    [Fact]
    public void EveryPageIsInTheNav()
    {
        var nav = NavEntries();

        Assert.NotEmpty(nav);
        AssertNone(EnglishPages().Except(nav, StringComparer.Ordinal).Select(page => $"{page} is not in site.yml's nav"));

        // The i18n plugin finds each page's twin itself; the nav names only the English page.
        Assert.DoesNotContain(nav, IsUkrainian);
    }

    /// <summary>Fails with every problem on a line of its own, where <c>Assert.Empty</c> would show five, each cut short.</summary>
    private static void AssertNone(IEnumerable<string> problems)
    {
        var list = problems.ToList();
        if (list.Count > 0)
        {
            Assert.Fail($"{list.Count} problem(s) in docs/:\n{string.Join('\n', list)}");
        }
    }

    /// <summary>Every page, English and Ukrainian, relative to <c>docs/</c> with <c>/</c> between folders.</summary>
    private static IEnumerable<string> AllPages() =>
        Directory.EnumerateFiles(Docs, "*.md", SearchOption.AllDirectories).Select(Relative).Order(StringComparer.Ordinal);

    private static IEnumerable<string> EnglishPages() => AllPages().Where(page => !IsUkrainian(page));

    private static bool IsUkrainian(string page) => page.EndsWith(UkrainianSuffix, StringComparison.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(Docs, path).Replace('\\', '/');

    private static string Full(string page) => Path.Combine(Docs, page);

    private static string[] Lines(string file) => File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n');

    /// <summary>A page's lines outside fenced code blocks: there a <c>#</c> or a <c>](</c> is sample text, not Markdown.</summary>
    private static IEnumerable<string> ProseLines(string page)
    {
        string? fence = null;
        foreach (var line in Lines(Full(page)))
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

    /// <summary>The ids of a page's headings, in order.</summary>
    private static List<string> Ids(string page) =>
        [.. ProseLines(page).Select(line => HeadingId().Match(line)).Where(match => match.Success).Select(match => match.Groups["id"].Value)];

    /// <summary>The H2–H4 headings with no explicit <c>{#id}</c> at their end, and the ids a page uses twice.</summary>
    private static IEnumerable<string> HeadingProblems(string page)
    {
        foreach (var line in ProseLines(page))
        {
            if (Heading().Match(line) is { Success: true } heading
                && heading.Groups["level"].Length is >= 2 and <= 4
                && !HeadingId().IsMatch(line))
            {
                yield return $"{page}: \"{line.Trim()}\" does not end with a lowercase ASCII id such as {{#some-section}}";
            }
        }

        foreach (var id in Ids(page).GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            yield return $"{page}: the id {id.Key} names more than one heading";
        }
    }

    /// <summary>The targets of a page's inline links and images, and of its reference definitions.</summary>
    private static IEnumerable<string> Links(string page)
    {
        foreach (var line in ProseLines(page).Select(line => CodeSpan().Replace(line, "")))
        {
            foreach (Match link in InlineLink().Matches(line))
            {
                yield return link.Groups["target"].Value;
            }

            if (LinkDefinition().Match(line) is { Success: true } definition)
            {
                yield return definition.Groups["target"].Value;
            }
        }
    }

    private static bool IsExternal(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why a relative link from <paramref name="page"/> breaks, or null when it works: the file it names must be in
    /// <c>docs/</c>, spelled with the file's own case (the site is built on Linux), and a section must be one of that
    /// page's ids. A link to a section of the same page (<c>#id</c>) is checked against the page's own ids.
    /// </summary>
    private static string? LinkProblem(string page, string target, HashSet<string> files)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var (path, section) = hash < 0 ? (target, "") : (target[..hash], target[(hash + 1)..]);

        var file = page;
        if (path.Length > 0)
        {
            var full = Path.GetFullPath(Path.Combine(Docs, Path.GetDirectoryName(page) ?? "", Uri.UnescapeDataString(path)));
            file = Relative(full);
            if (file.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(file))
            {
                return "leaves docs/; link to a file outside it with https://github.com/Sigil-build/dictum/blob/HEAD/…";
            }

            if (!files.Contains(file))
            {
                return $"names {file}, which does not exist";
            }
        }

        return section.Length > 0 && file.EndsWith(".md", StringComparison.Ordinal) && !Ids(file).Contains(section)
            ? $"names the section {section}, which {file} does not have"
            : null;
    }

    /// <summary>The pages in <c>site.yml</c>'s nav: its <c>.md</c> entries, from the line <c>nav:</c> to the next key.</summary>
    private static List<string> NavEntries()
    {
        var entries = new List<string>();
        var inNav = false;
        foreach (var line in Lines(SiteFile))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] is not ('#' or '-'))
            {
                inNav = line.StartsWith("nav:", StringComparison.Ordinal);
            }
            else if (inNav && NavEntry().Match(line) is { Success: true } entry)
            {
                entries.Add(entry.Groups["path"].Value);
            }
        }

        return entries;
    }

    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*(?<level>#{1,6})\s")]
    private static partial Regex Heading();

    /// <summary>A heading that ends with an explicit lowercase ASCII id, as in <c>## How it works {#how-it-works}</c>.</summary>
    [GeneratedRegex(@"^\s*#{1,6}\s.*\s\{#(?<id>[a-z0-9]+(?:-[a-z0-9]+)*)\}\s*$")]
    private static partial Regex HeadingId();

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex CodeSpan();

    /// <summary><c>[text](target)</c> or <c>![alt](target "title")</c>.</summary>
    [GeneratedRegex(@"\]\(\s*<?(?<target>[^)\s>]+)>?(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)")]
    private static partial Regex InlineLink();

    /// <summary><c>[name]: target</c>.</summary>
    [GeneratedRegex(@"^\s{0,3}\[[^\]]+\]:\s*<?(?<target>[^\s>]+)")]
    private static partial Regex LinkDefinition();

    [GeneratedRegex(@"^version:\s*""(?<version>[^""]*)""\s*(?:#.*)?$")]
    private static partial Regex SiteVersion();

    /// <summary><c>- page.md</c>, or <c>- Title: page.md</c>, in the nav.</summary>
    [GeneratedRegex(@"^\s*-\s+(?:[^:#]+:\s+)?[""']?(?<path>[^\s""':#]+\.md)[""']?\s*(?:#.*)?$")]
    private static partial Regex NavEntry();
}
