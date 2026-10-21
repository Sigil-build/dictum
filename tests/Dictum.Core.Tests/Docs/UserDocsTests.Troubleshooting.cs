namespace Dictum.Core.Tests.Docs;

public partial class UserDocsTests
{
    private const string TroubleshootingPage = "troubleshooting.md";

    /// <summary>
    /// The sections of the troubleshooting page that the app and <c>dictum doctor</c> link to, in the page's order, with
    /// each heading's level. The release bakes these ids into its URLs, so a section can move within the page or gain
    /// neighbours, but it keeps its id and level, and an H3 stays under the H2 listed before it.
    /// </summary>
    private static readonly (string Id, int Level)[] TroubleshootingSections =
    [
        ("doctor", 2),
        ("config", 2),
        ("config-warnings", 2),
        ("processor-in-no-profile", 3),
        ("models", 2),
        ("microphone", 2),
        ("hotkeys", 2),
        ("pasting", 2),
        ("administrator", 2),
        ("secrets", 2),
        ("desktop-session", 2),
        ("daemon", 2),
        ("speech-recognition", 2),
        ("llm-cleanup", 2),
        ("translated-words", 2),
        ("smartscreen", 2),
        ("logs", 2),
    ];

    // The Ukrainian twin has the same ids in the same order: EveryPageHasAUkrainianTwinWithTheSameSections checks that.
    [Fact]
    public void TroubleshootingHasEverySection()
    {
        Assert.True(File.Exists(Full(TroubleshootingPage)), $"docs/{TroubleshootingPage} does not exist");

        var headings = ProseLines(TroubleshootingPage)
            .Select(line => (Level: Heading().Match(line), Id: HeadingId().Match(line)))
            .Where(heading => heading.Level.Success && heading.Id.Success)
            .Select(heading => (Id: heading.Id.Groups["id"].Value, Level: heading.Level.Groups["level"].Length))
            .ToList();

        var problems = new List<string>();
        var from = 0;
        string? section = null;
        foreach (var (id, level) in TroubleshootingSections)
        {
            var at = headings.FindIndex(from, heading => heading.Id == id);
            if (at < 0)
            {
                problems.Add(headings.Exists(heading => heading.Id == id)
                    ? $"{TroubleshootingPage}: {{#{id}}} comes before a section that the list puts ahead of it"
                    : $"{TroubleshootingPage}: no heading has the id {{#{id}}}");
                continue;
            }

            if (headings[at].Level != level)
            {
                problems.Add($"{TroubleshootingPage}: {{#{id}}} is an H{headings[at].Level}, not an H{level}");
            }

            if (level == 2)
            {
                section = id;
            }
            else if (headings.Take(at).LastOrDefault(heading => heading.Level == 2).Id is var parent && parent != section)
            {
                problems.Add($"{TroubleshootingPage}: {{#{id}}} sits under {{#{parent}}}, not under {{#{section}}}");
            }

            from = at + 1;
        }

        AssertNone(problems);
    }
}
