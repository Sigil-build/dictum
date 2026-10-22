using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Dictum.Core.Docs;
using Dictum.Testing;

namespace Dictum.Core.Tests.Docs;

/// <summary>
/// The documentation URLs the app and <c>dictum doctor</c> open. Each release keeps them for years, so their scheme and
/// sections are fixed here, and each one must name a section that both languages' pages in <c>docs/</c> have.
/// </summary>
public sealed partial class DocsLinksTests
{
    private static string Docs => Path.Combine(Repository.Root, "docs");

    [Theory]
    [InlineData(DocsPage.Home, "en", null, "https://docs.sigil.build/dictum/")]
    [InlineData(DocsPage.Home, "uk", null, "https://docs.sigil.build/uk/dictum/")]
    [InlineData(DocsPage.Settings, "en", "pasting", "https://docs.sigil.build/dictum/reference/settings/#pasting")]
    [InlineData(DocsPage.Troubleshooting, "uk", "hotkeys", "https://docs.sigil.build/uk/dictum/troubleshooting/#hotkeys")]
    [InlineData(DocsPage.Privacy, "uk", null, "https://docs.sigil.build/uk/dictum/privacy/")]
    public void TheUrlsFollowTheSiteScheme(DocsPage page, string language, string? anchor, string expected) =>
        Assert.Equal(expected, DocsLinks.For(page, language, anchor).AbsoluteUri);

    [Fact]
    public void ALanguageTheSiteDoesNotHaveIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DocsLinks.For(DocsPage.Home, "fr"));

    // general.uiLanguage wins when it is one the app speaks; with none set, the system's UI language decides, and
    // English serves every language the site has no pages in.
    [Theory]
    [InlineData("uk", "en-US", "uk")]
    [InlineData(null, "uk-UA", "uk")]
    [InlineData("en", "uk-UA", "en")]
    [InlineData(null, "de-DE", "en")]
    [InlineData(null, "", "en")]
    [InlineData("fr", "uk-UA", "en")]
    public void TheLanguageIsTheUiLanguageOrTheSystems(string? uiLanguage, string osUiCulture, string expected) =>
        Assert.Equal(expected, DocsLinks.Language(uiLanguage, CultureInfo.GetCultureInfo(osUiCulture)));

    /// <summary>The Settings window's pages, each a section of the settings reference.</summary>
    private static readonly string[] SettingsSections =
        ["general", "overlay", "pasting", "profiles", "speech-recognition", "processors", "models", "history", "system", "about"];

    /// <summary>The troubleshooting sections that doctor's rows link to.</summary>
    private static readonly string[] TroubleshootingSections =
    [
        "doctor", "config", "config-warnings", "models", "microphone", "hotkeys", "pasting", "administrator", "secrets",
        "desktop-session", "daemon", "speech-recognition", "llm-cleanup",
    ];

    // Released programs open these URLs, so a section keeps its id even when its heading is reworded.
    [Fact]
    public void TheTargetsAreTheFixedSections()
    {
        string[] expected =
        [
            "dictum/",
            "dictum/#how-it-works",
            .. SettingsSections.Select(id => $"dictum/reference/settings/#{id}"),
            .. TroubleshootingSections.Select(id => $"dictum/troubleshooting/#{id}"),
            "dictum/privacy/",
        ];

        Assert.Equal(expected, DocsLinks.Targets.Select(target => DocsLinks.For(target.Page, "en", target.Anchor).AbsoluteUri[DocsLinks.Root.Length..]));
    }

    // A section the code can link to is a target, so EveryTargetExistsInBothLanguages checks it too.
    [Fact]
    public void EveryAnchorIsATarget()
    {
        var anchors = Constants(typeof(DocsAnchors)).Select(id => new DocsTarget(DocsPage.Home, id))
            .Concat(Constants(typeof(DocsAnchors.Settings)).Select(id => new DocsTarget(DocsPage.Settings, id)))
            .Concat(Constants(typeof(DocsAnchors.Troubleshooting)).Select(id => new DocsTarget(DocsPage.Troubleshooting, id)));

        Assert.Empty(anchors.Except(DocsLinks.Targets));
        Assert.Equal(DocsLinks.Targets.Count, DocsLinks.Targets.Distinct().Count());
    }

    [Fact]
    public void EveryTargetExistsInBothLanguages()
    {
        var problems = new List<string>();
        foreach (var target in DocsLinks.Targets)
        {
            foreach (var (language, prefix, suffix) in new[] { ("en", "dictum/", ".md"), ("uk", "uk/dictum/", ".uk.md") })
            {
                var url = DocsLinks.For(target.Page, language, target.Anchor);
                if (Problem(url, DocsLinks.Root + prefix, suffix) is { } problem)
                {
                    problems.Add($"{url.AbsoluteUri}: {problem}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// Why <paramref name="url"/> has no page or section behind it, or null when it has: the site serves
    /// <c>dictum/</c> from <c>index.md</c> and <c>dictum/x/y/</c> from <c>x/y.md</c>, and a section is a heading's
    /// <c>{#id}</c>.
    /// </summary>
    private static string? Problem(Uri url, string prefix, string suffix)
    {
        var path = url.GetLeftPart(UriPartial.Path);
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith('/'))
        {
            return $"is not a page under {prefix}";
        }

        var page = path[prefix.Length..].TrimEnd('/') is { Length: > 0 } name ? name + suffix : "index" + suffix;
        var file = Path.Combine(Docs, page);
        if (!File.Exists(file))
        {
            return $"docs/{page} does not exist";
        }

        var anchor = url.Fragment.TrimStart('#');
        return anchor.Length > 0 && !HeadingIds(file).Contains(anchor) ? $"docs/{page} has no heading {{#{anchor}}}" : null;
    }

    // A heading inside a code block is sample text: fences open with ``` or ~~~, and a longer fence holds shorter ones.
    [Fact]
    public void HeadingIdsSkipEveryKindOfFence()
    {
        using var dir = new TempDirectory();
        var file = dir.File("page.md");
        File.WriteAllLines(file,
        [
            "## Real {#real}",
            "~~~markdown",
            "## In a tilde fence {#tilde}",
            "~~~",
            "````markdown",
            "```",
            "## In a longer fence {#longer}",
            "```",
            "````",
            "## After {#after}",
        ]);

        Assert.Equal(["after", "real"], HeadingIds(file).Order(StringComparer.Ordinal));
    }

    /// <summary>The <c>{#id}</c>s of a page's headings, outside fenced code blocks.</summary>
    private static HashSet<string> HeadingIds(string file)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in MarkdownFences.ProseLines(File.ReadAllLines(file)))
        {
            if (HeadingId().Match(line) is { Success: true } heading)
            {
                ids.Add(heading.Groups["id"].Value);
            }
        }

        return ids;
    }

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    [GeneratedRegex(@"^#{1,6}\s.*\s\{#(?<id>[a-z0-9-]+)\}\s*$")]
    private static partial Regex HeadingId();
}
