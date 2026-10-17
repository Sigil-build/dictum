using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Settings;

public sealed class RulesEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();

    public RulesEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (RulesEditor Editor, SettingsSession Session) Open()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        store.Save(ProfilesPageTests.Sample());
        var session = new SettingsSession(store, _timers, NullLog.Instance);
        session.Load();
        var editor = new RulesEditor(session);
        editor.Load("terms", session.Config);
        session.Changed += () => editor.Load("terms", session.Config);
        return (editor, session);
    }

    private static List<ReplaceRule> Rules(SettingsSession session) => ((ReplaceProcessorConfig)session.Config.Processors["terms"]).Rules;

    [Fact]
    public void TheRulesComeFirstThenTheBlankRow()
    {
        var (editor, _) = Open();

        Assert.Equal(2, editor.Rows.Count);
        Assert.Equal(("кубер", "Kubernetes", false, false, true, false), (editor.Rows[0].Find, editor.Rows[0].Replace, editor.Rows[0].MatchCase,
            editor.Rows[0].WholeWord, editor.Rows[0].On, editor.Rows[0].IsDraft));
        Assert.True(editor.Rows[1].IsDraft);
    }

    // Review focus 1.
    [Fact]
    public void TypingInTheBlankRowAddsARule()
    {
        var (editor, session) = Open();
        var draft = editor.Rows[1];

        draft.Replace = "main";
        draft.WholeWord = true;
        Assert.Single(Rules(session));

        draft.Find = "мейн";

        Assert.Equal([("кубер", "Kubernetes"), ("мейн", "main")], Rules(session).Select(r => (r.From, r.To)));
        Assert.True(Rules(session)[1].WholeWord);
        Assert.Same(draft, editor.Rows[1]);
        Assert.False(draft.IsDraft);
        Assert.True(editor.Rows[2].IsDraft);
        Assert.False(session.HasErrors);
    }

    // Review focus 1.
    [Fact]
    public void AClearedFindIsShownNotSaved()
    {
        var (editor, session) = Open();

        editor.Rows[0].Find = "";

        Assert.Equal(Strings.SettingsRulesFindEmpty, editor.Rows[0].Error);
        Assert.True(session.HasErrors);
        Assert.Equal(0, _timers.Pending);
    }

    // Review focus 1.
    [Fact]
    public void ABadRegexIsShownOnItsRow()
    {
        var (editor, _) = Open();

        editor.Rows[0].IsRegex = true;
        editor.Rows[0].Find = "(кубер";

        Assert.StartsWith("invalid regex", editor.Rows[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RegexMovesTheTextBetweenFindAndPattern()
    {
        var (editor, session) = Open();

        editor.Rows[0].IsRegex = true;
        Assert.Equal(((string?)null, "кубер"), (Rules(session)[0].From, Rules(session)[0].Pattern));
        Assert.False(editor.Rows[0].CanWholeWord);

        editor.Rows[0].IsRegex = false;
        Assert.Equal(("кубер", (string?)null), (Rules(session)[0].From, Rules(session)[0].Pattern));
    }

    [Fact]
    public void TheFlagsAreSavedAndRowsStayInPlace()
    {
        var (editor, session) = Open();
        var row = editor.Rows[0];

        row.MatchCase = true;
        row.WholeWord = true;
        row.On = false;
        row.Replace = "K8s";

        var rule = Rules(session)[0];
        Assert.Equal((false, true, true, "K8s"), (rule.IgnoreCase, rule.WholeWord, rule.Disabled, rule.To));
        Assert.Same(row, editor.Rows[0]);
    }

    [Fact]
    public void ARowCanBeRemoved()
    {
        var (editor, session) = Open();

        editor.Rows[0].RemoveCommand.Execute(null);

        Assert.Empty(Rules(session));
        Assert.Single(editor.Rows);
        Assert.False(editor.Rows[0].RemoveCommand.CanExecute(null));
    }

    [Fact]
    public void ImportAddsTheGoodRowsAndNamesTheBadOnes()
    {
        var (editor, session) = Open();

        editor.ImportCsv("дот нет,.NET\n,nothing\n");

        Assert.Equal(["кубер", "дот нет"], Rules(session).Select(r => r.From));
        Assert.Equal("Rules imported: 1. Skipped: Line 2: nothing to find.", editor.ImportNote);
    }


    // Final review M2: Regex ticked on an empty Find must stay ticked, or the pattern is saved as plain text.
    [Fact]
    public void RegexTickedOnAnEmptyFindSticks()
    {
        var (editor, session) = Open();
        editor.Rows[0].Find = "";

        editor.Rows[0].IsRegex = true;
        editor.Rows[0].Find = "a+";

        Assert.Equal(((string?)null, "a+"), (Rules(session)[0].From, Rules(session)[0].Pattern));
        Assert.True(editor.Rows[0].IsRegex);
    }
}
