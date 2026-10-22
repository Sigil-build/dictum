using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Dictum.App.Resources;

namespace Dictum.App.Tests.Docs;

/// <summary>
/// The reference pages in <c>docs/reference/</c> against the strings the app shows, in English and in Ukrainian: each
/// Settings field, wizard step and tray menu item is there under the name the user reads on screen, and each field the
/// settings page names is still in the app. A renamed label fails here until the page follows it.
/// </summary>
public sealed partial class ReferenceDocsTests
{
    private const string SettingsViews = "src/Dictum.App/Views/Settings";

    /// <summary>
    /// The Settings pages in the rail's order, with General's two sub-pages after it: the H3 id that the app and other
    /// pages link to, the key of the page's title, which heads the H3, and the views that draw the page.
    /// </summary>
    private static readonly SettingsSection[] Sections =
    [
        new("general", nameof(Strings.SettingsGeneral), ["GeneralView.axaml"]),
        new("overlay", nameof(Strings.SettingsOverlay), ["OverlayView.axaml"]),
        new("pasting", nameof(Strings.SettingsPasting), ["PastingView.axaml"]),
        new("profiles", nameof(Strings.SettingsProfiles), ["ProfilesView.axaml"]),
        new("speech-recognition", nameof(Strings.SettingsSpeechProviders), ["SpeechProvidersView.axaml", "ApiKeyRow.axaml"]),
        new("processors", nameof(Strings.SettingsProcessors), ["ProcessorsView.axaml", "ApiKeyRow.axaml"]),
        new("models", nameof(Strings.SettingsModels), ["ModelsView.axaml"]),
        new("system", nameof(Strings.SettingsSystem), ["SystemView.axaml"]),
        new("history", nameof(Strings.SettingsHistory), ["HistoryView.axaml"]),
        new("about", nameof(Strings.SettingsAbout), ["AboutView.axaml"]),
    ];

    /// <summary>The window around the pages; it has no fields of its own.</summary>
    private const string SettingsWindow = "SettingsWindow.axaml";

    // The app links to each page's H3 by id, and the rail is the order the reader sees the pages in.
    [Theory, InlineData("en"), InlineData("uk")]
    public void EverySettingsPageIsASectionInRailOrder(string lang)
    {
        var page = Page("settings", lang);
        var problems = new List<string>();

        var expected = Sections.Select(s => $"{s.Id}: {Text(s.TitleKey, lang)}").ToList();
        var actual = page.Sections.Select(s => $"{s.Id}: {s.Title}").ToList();
        if (!expected.SequenceEqual(actual))
        {
            problems.Add($"{page.Name} has the H3s [{string.Join(", ", actual)}], but Settings has [{string.Join(", ", expected)}]");
        }

        var tabled = Sections.SelectMany(s => s.Views).Append(SettingsWindow).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(Directory.EnumerateFiles(Path.Combine(Repository.Root, SettingsViews), "*.axaml")
            .Select(Path.GetFileName).OfType<string>()
            .Where(view => !tabled.Contains(view))
            .Select(view => $"{SettingsViews}/{view} is in no section of {nameof(ReferenceDocsTests)}.{nameof(Sections)}"));

        AssertNone(problems);
    }

    // A field's label is its SettingsRow label, or a string whose resx has a "<key>Hint" description beside it.
    [Theory, InlineData("en"), InlineData("uk")]
    public void EverySettingsFieldIsDocumented(string lang)
    {
        var page = Page("settings", lang);
        var problems = new List<string>();
        foreach (var section in Sections)
        {
            var fields = page.Section(section.Id)?.Fields ?? [];
            foreach (var key in Labels(section).Where(key => !HasPlaceholder(Text(key, lang))))
            {
                if (!fields.Contains(Text(key, lang)))
                {
                    problems.Add($"{page.Name}: the section {section.Id} has no H4 \"{Text(key, lang)}\" ({key})");
                }
            }
        }

        AssertNone(problems);
    }

    [Theory, InlineData("en"), InlineData("uk")]
    public void EveryDocumentedFieldIsInTheApp(string lang)
    {
        var page = Page("settings", lang);
        var problems = new List<string>();
        foreach (var section in page.Sections)
        {
            if (Sections.FirstOrDefault(s => s.Id == section.Id) is not { } known)
            {
                problems.AddRange(section.Fields.Select(field => $"{page.Name}: the H4 \"{field}\" is under {section.Id}, which is no Settings page"));
                continue;
            }

            var shown = UsedKeys(Source(known)).Select(key => Text(key, lang)).ToHashSet(StringComparer.Ordinal);
            problems.AddRange(section.Fields.Where(field => !shown.Contains(field))
                .Select(field => $"{page.Name}: the H4 \"{field}\" under {section.Id} is no string that {string.Join(" or ", known.Views)} shows"));
        }

        problems.AddRange(page.Orphans.Select(field => $"{page.Name}: the H4 \"{field}\" is under no Settings page's H3"));
        AssertNone(problems);
    }

    [Theory, InlineData("en"), InlineData("uk")]
    public void EveryWizardStepIsDocumented(string lang)
    {
        var page = Page("setup-wizard", lang);
        var steps = Keys().Where(key => key.StartsWith("SetupStep", StringComparison.Ordinal)).Select(key => Text(key, lang)).Where(text => !HasPlaceholder(text));

        AssertNone(steps.Where(step => !page.Chapters.Contains(step)).Select(step => $"{page.Name} has no H2 \"{step}\""));
    }

    [Theory, InlineData("en"), InlineData("uk")]
    public void EveryTrayMenuItemIsDocumented(string lang)
    {
        var page = Page("tray-and-overlay", lang);
        var items = Keys().Where(key => key.StartsWith("Menu", StringComparison.Ordinal)).Select(key => Text(key, lang)).Where(text => !HasPlaceholder(text));

        AssertNone(items.Where(item => !page.Text.Contains(item, StringComparison.Ordinal)).Select(item => $"{page.Name} never names the menu item \"{item}\""));
    }

    /// <summary>The keys a section expects an H4 for: its SettingsRow labels and the strings that have a description, but not the page's own title.</summary>
    private static IEnumerable<string> Labels(SettingsSection section)
    {
        var source = Source(section);
        var rows = RowLabel().Matches(source).Select(match => match.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
        return UsedKeys(source).Where(key => key != section.TitleKey && (rows.Contains(key) || Exists(key + "Hint")));
    }

    /// <summary>The section's views, one after another.</summary>
    private static string Source(SettingsSection section) =>
        string.Concat(section.Views.Select(view => File.ReadAllText(Path.Combine(Repository.Root, SettingsViews, view))));

    /// <summary>Every <c>Strings.Key</c> the source uses, in the order it first uses them.</summary>
    private static IEnumerable<string> UsedKeys(string source) =>
        StringUse().Matches(source).Select(match => match.Groups["key"].Value).Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> Keys() =>
        Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Order(StringComparer.Ordinal);

    private static bool Exists(string key) => Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture) is not null;

    /// <summary>The string as the app shows it in <paramref name="lang"/>.</summary>
    private static string Text(string key, string lang) =>
        Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo(lang)) ?? throw new InvalidOperationException($"No string {key}");

    /// <summary>A string with a <c>{0}</c> is filled in at run time, so the page cannot quote it as the app shows it.</summary>
    private static bool HasPlaceholder(string text) => Placeholder().IsMatch(text);

    private static ReferencePage Page(string name, string lang) => ReferencePage.Read(name, lang);

    /// <summary>Fails with every problem on a line of its own.</summary>
    private static void AssertNone(IEnumerable<string> problems)
    {
        var list = problems.ToList();
        if (list.Count > 0)
        {
            Assert.Fail($"{list.Count} problem(s) in docs/reference/:\n{string.Join('\n', list)}");
        }
    }

    [GeneratedRegex(@"\bStrings\.(?<key>\w+)\b")]
    private static partial Regex StringUse();

    /// <summary><c>&lt;controls:SettingsRow … Label="{x:Static res:Strings.Key}"</c>, its attributes on any number of lines.</summary>
    [GeneratedRegex(@"<controls:SettingsRow\s[^>]*?\bLabel=""\{x:Static res:Strings\.(?<key>\w+)\}""")]
    private static partial Regex RowLabel();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    /// <param name="Id">The H3's id in <c>reference/settings.md</c>.</param>
    /// <param name="TitleKey">The page's title, the text of its rail item and of the H3.</param>
    /// <param name="Views">The <c>.axaml</c> files in <see cref="SettingsViews"/> that draw the page.</param>
    private sealed record SettingsSection(string Id, string TitleKey, string[] Views);
}

/// <summary>An H3 of a reference page and the texts of the H4s under it.</summary>
internal sealed record PageSection(string Id, string Title, List<string> Fields);

/// <summary>
/// A page in <c>docs/reference/</c> as the checks read it: headings outside code blocks, each heading's text without
/// the <c>#</c>s and without its <c>{#id}</c>.
/// </summary>
internal sealed partial class ReferencePage
{
    private ReferencePage(string name, string text)
    {
        Name = name;
        Text = text;
    }

    /// <summary>The file, relative to <c>docs/</c>.</summary>
    public string Name { get; }

    public string Text { get; }

    /// <summary>The H2s' texts.</summary>
    public List<string> Chapters { get; } = [];

    /// <summary>The H3s, each with the H4s up to the next H2 or H3.</summary>
    public List<PageSection> Sections { get; } = [];

    /// <summary>H4s before any H3, or after an H2 that ended the last H3.</summary>
    public List<string> Orphans { get; } = [];

    public PageSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    public static ReferencePage Read(string name, string lang)
    {
        var file = $"reference/{name}{(lang == "en" ? "" : "." + lang)}.md";
        var path = Path.Combine(Repository.Root, "docs", file);
        Assert.True(File.Exists(path), $"docs/{file} does not exist");

        var page = new ReferencePage(file, File.ReadAllText(path).ReplaceLineEndings("\n"));
        PageSection? current = null;
        foreach (var line in page.ProseLines())
        {
            if (Heading().Match(line) is not { Success: true } heading)
            {
                continue;
            }

            var text = heading.Groups["text"].Value.Trim();
            switch (heading.Groups["level"].Length)
            {
                case 2:
                    page.Chapters.Add(text);
                    current = null;
                    break;
                case 3:
                    current = new PageSection(heading.Groups["id"].Value, text, []);
                    page.Sections.Add(current);
                    break;
                case 4 when current is not null:
                    current.Fields.Add(text);
                    break;
                case 4:
                    page.Orphans.Add(text);
                    break;
            }
        }

        return page;
    }

    /// <summary>The lines outside fenced code blocks, where a <c>#</c> is sample text.</summary>
    private IEnumerable<string> ProseLines()
    {
        string? fence = null;
        foreach (var line in Text.Split('\n'))
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

    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    private static partial Regex Fence();

    /// <summary><c>#### Label {#id}</c>: the level, the text and the id, which may be missing.</summary>
    [GeneratedRegex(@"^\s*(?<level>#{1,6})\s+(?<text>.*?)(?:\s+\{#(?<id>[^}\s]+)\})?\s*$")]
    private static partial Regex Heading();
}
