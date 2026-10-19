using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;

namespace Dictum.App.Tests.Settings;

public sealed class HistoryPageTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _dir = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeRunner _runner = new();

    private string File => _dir.File("history.jsonl");

    public void Dispose() => _dir.Dispose();

    private void Dictated(int minute, string raw, string text, string profile = "uk", string? language = "uk", string? error = null) =>
        new HistoryStore(File, 200).Append(new HistoryEntry(Start.AddMinutes(minute), profile, "local", raw, text, language, 2.46, error));

    private HistoryPage Open(int historySize = 200)
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        var config = ProfilesPageTests.Sample();
        config.General = config.General with { HistorySize = historySize };
        store.Save(config);
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var page = new HistoryPage(session, File, _clipboard, _runner, NullLog.Instance);
        page.OnShown();
        return page;
    }

    private void ThreeDictations()
    {
        Dictated(1, "привіт світ", "Привіт, світ.");
        Dictated(2, "hello there", "Hello there.", profile: "en", language: "en");
        Dictated(3, "третій запис\nдругий рядок", "Третій запис.\nДругий рядок.");
    }

    [Fact]
    public void TheNewestComesFirstAndIsSelected()
    {
        ThreeDictations();

        var page = Open();

        Assert.Equal(["Третій запис.", "Hello there.", "Привіт, світ."], page.Items.Select(i => i.FirstLine));
        Assert.Equal(page.Items[0], page.Selected);
        Assert.Equal(("uk", "UK", "2.5 s"), (page.Items[0].Profile, page.Items[0].Language, page.Items[0].Duration));
        Assert.Null(page.Empty);
    }

    [Fact]
    public void TheHistorySizeIsHonoured()
    {
        ThreeDictations();

        Assert.Equal(2, Open(historySize: 2).Items.Count);
    }

    [Fact]
    public void HistoryOffShowsNothingAndSaysSo()
    {
        ThreeDictations();

        var page = Open(historySize: 0);

        Assert.Empty(page.Items);
        Assert.Equal(Strings.SettingsHistoryOff, page.Empty);
    }

    [Fact]
    public void WithoutAFileThereIsNothingYet()
    {
        var page = Open();

        Assert.Empty(page.Items);
        Assert.Equal(Strings.SettingsHistoryNone, page.Empty);
        Assert.Null(page.Error);
    }

    [Fact]
    public void SearchIgnoresCaseAndLooksAtBothTextsAndTheProfile()
    {
        ThreeDictations();
        var page = Open();

        page.Search = "ПРИВІТ";
        Assert.Equal(["Привіт, світ."], page.Items.Select(i => i.FirstLine));

        page.Search = "hello there";
        Assert.Single(page.Items);

        page.Search = "en";
        Assert.Equal(["Hello there."], page.Items.Select(i => i.FirstLine));
    }

    [Fact]
    public void ASearchWithoutMatchesSaysSo()
    {
        ThreeDictations();
        var page = Open();

        page.Search = "zebra";

        Assert.Empty(page.Items);
        Assert.Null(page.Selected);
        Assert.Equal("Nothing matches “zebra”.", page.Empty);
    }

    [Fact]
    public async Task CopyPutsTheFinalTextOnTheClipboard()
    {
        ThreeDictations();
        var page = Open();

        await page.CopyCommand.ExecuteAsync(page.Items[1]);

        Assert.Equal(["Hello there."], _clipboard.Texts);
    }

    // A busy clipboard throws; the page says so instead of crashing dictum-gui.
    [Fact]
    public async Task ACopyThatFailsIsShownNotThrown()
    {
        ThreeDictations();
        var page = Open();
        _clipboard.Fails = new InvalidOperationException("The clipboard is in use by another application.");

        await page.CopyCommand.ExecuteAsync(page.Items[0]);

        Assert.Equal("The text could not be copied: The clipboard is in use by another application.", page.Error);
    }

    [Fact]
    public void DeleteRemovesTheEntryFromTheFile()
    {
        ThreeDictations();
        var page = Open();

        page.DeleteCommand.Execute(page.Items[1]);

        Assert.Equal(["Третій запис.", "Привіт, світ."], page.Items.Select(i => i.FirstLine));
        Assert.Equal(2, new HistoryStore(File, 200).ReadLast(10).Count);
        Assert.Equal(page.Items[0], page.Selected);
    }

    [Fact]
    public void AFailedDictationShowsWhy()
    {
        Dictated(1, "raw", "raw", error: "cleanup: timed out");

        var item = Open().Items[0];

        Assert.Equal("Failed: cleanup: timed out", item.Problem);
    }

    [Fact]
    public void ShowingThePageAgainReadsNewDictations()
    {
        Dictated(1, "один", "Один.");
        var page = Open();
        Dictated(2, "два", "Два.");

        page.OnShown();

        Assert.Equal(2, page.Items.Count);
    }

    private sealed class FakeClipboard : ITextClipboard
    {
        public List<string> Texts { get; } = [];

        public Exception? Fails { get; set; }

        public Task SetTextAsync(string text)
        {
            if (Fails is not null)
            {
                return Task.FromException(Fails);
            }

            Texts.Add(text);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void TheChainsAreTheProfilesThatHaveSteps()
    {
        ThreeDictations();

        var page = Open();

        Assert.Equal(["Work: cleanup → terms"], page.Chains.Select(c => c.Label));
        Assert.True(page.HasChains);
    }

    [Fact]
    public void AReRunShowsTheResultAndLeavesTheFileAlone()
    {
        ThreeDictations();
        var before = System.IO.File.ReadAllBytes(File);
        var page = Open();

        page.Chain = page.Chains[0];

        var run = _runner.Runs.Single();
        Assert.Equal("третій запис\nдругий рядок", run.Text);
        Assert.Equal(["cleanup", "terms"], run.Steps.Select(s => s.Id));
        Assert.Equal(("work", "uk, en", "Nexus5, Avalonia"), (run.Context.ProfileId, run.Context.Language, run.Context.Vocabulary));
        Assert.Equal(("ТРЕТІЙ ЗАПИС\nДРУГИЙ РЯДОК", "Through Work · 42 ms"), (page.ReRunText, page.ReRunLabel));
        Assert.Equal(before, System.IO.File.ReadAllBytes(File));
        Assert.Empty(_clipboard.Texts);
    }

    [Fact]
    public void ANewerReRunWins()
    {
        ThreeDictations();
        var page = Open();
        var first = new TaskCompletionSource<RunResult>();
        _runner.Held.Enqueue(first);

        page.Chain = page.Chains[0];
        page.Chain = null;
        _runner.Answer = _ => new RunResult("newer", 5, null);
        page.Chain = page.Chains[0];
        first.SetResult(new RunResult("older", 900, null));

        Assert.Equal("newer", page.ReRunText);
    }

    [Fact]
    public void ChoosingAnotherDictationClearsTheResult()
    {
        ThreeDictations();
        var page = Open();
        page.Chain = page.Chains[0];

        page.Selected = page.Items[1];

        Assert.Null(page.ReRunText);
        Assert.Null(page.ReRunLabel);
    }

    [Fact]
    public async Task TheResultCanBeCopied()
    {
        ThreeDictations();
        var page = Open();
        page.Chain = page.Chains[0];

        await page.CopyReRunCommand.ExecuteAsync(null);

        Assert.Equal(["ТРЕТІЙ ЗАПИС\nДРУГИЙ РЯДОК"], _clipboard.Texts);
    }

    [Fact]
    public async Task ACopyThatWorksClearsTheLastCopysError()
    {
        ThreeDictations();
        var page = Open();
        page.Chain = page.Chains[0];
        _clipboard.Fails = new InvalidOperationException("The clipboard is in use by another application.");
        await page.CopyCommand.ExecuteAsync(page.Items[0]);
        _clipboard.Fails = null;

        await page.CopyReRunCommand.ExecuteAsync(null);

        Assert.Null(page.Error);
    }

    [Fact]
    public void AFailedReRunShowsWhy()
    {
        ThreeDictations();
        _runner.Answer = text => new RunResult(text, 3, "Connection refused");
        var page = Open();

        page.Chain = page.Chains[0];

        Assert.Equal("Connection refused", page.ReRunError);
    }
}
