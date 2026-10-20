using System.Text;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Processing;

namespace Dictum.App.Tests.Settings;

public sealed class ProcessorsPageTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ManualTimers _timers = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeSecrets _secrets = new();
    private readonly PromptLibrary _prompts;

    public ProcessorsPageTests()
    {
        _prompts = new PromptLibrary(_dir.File("prompts"));
    }

    private ConfigStore Store => new(_dir.File("config.json"), NullLog.Instance);

    public void Dispose() => _dir.Dispose();

    /// <summary>The page on screen; <paramref name="shown"/> false is a window opened on another page.</summary>
    private (ProcessorsPage Page, SettingsSession Session) Open(bool shown = true, string? cleanupKey = null)
    {
        var config = ProfilesPageTests.Sample();
        config.Processors["cleanup"] = (LlmProcessorConfig)config.Processors["cleanup"] with { ApiKey = cleanupKey };
        Store.Save(config);
        var session = new SettingsSession(Store, _timers, NullLog.Instance);
        session.Load();
        var page = new ProcessorsPage(session, new ProcessorsServices(_runner, _secrets, _prompts, _timers, NullLog.Instance));
        if (shown)
        {
            page.OnShown();
        }

        return (page, session);
    }

    private static LlmProcessorConfig Llm(SettingsSession session, string id = "cleanup") => (LlmProcessorConfig)session.Config.Processors[id];

    [Fact]
    public void TheListShowsEachProcessorWithItsKind()
    {
        var (page, _) = Open();

        Assert.Equal(["cleanup", "terms", "filler"], page.Processors.Select(p => p.Id));
        Assert.Equal(["LLM prompt · cleanup", "Rules: 1", "Rules: 0"], page.Processors.Select(p => p.Caption));
        Assert.True(page.IsLlm);
        Assert.Equal("Used by: Work", page.UsedBy);
        Assert.False(page.CanRemove);
    }

    [Fact]
    public void TheLlmEditorShowsTheProcessorAndItsPrompt()
    {
        var (page, _) = Open();

        Assert.Equal(("http://localhost:11434/v1", "qwen2.5:7b", "cleanup"), (page.Llm.Url, page.Llm.Model, page.Llm.Prompt.Selected));
        Assert.Contains("{{text}}", page.Llm.Prompt.Text, StringComparison.Ordinal);
        Assert.Equal("A built-in prompt. Editing it saves your version as prompts/cleanup.md.", page.Llm.Prompt.Note);
        Assert.Contains("cleanup", page.Llm.Prompt.Names);
    }

    [Fact]
    public void LlmFieldsAreSaved()
    {
        var (page, session) = Open();

        page.Llm.Model = "qwen3:8b";
        page.Llm.Temperature = 0.7m;
        page.Llm.TimeoutSeconds = 9;
        page.Llm.OnError = page.Llm.ErrorModes.Single(m => m.Value == LlmErrorMode.Fail);
        page.Llm.Wording = page.Llm.Wordings.Single(w => w.Value == true);
        page.Llm.MaxTokens = 300;

        var llm = Llm(session);
        Assert.Equal(("qwen3:8b", 0.7, 9000, LlmErrorMode.Fail, (bool?)true, (int?)300),
            (llm.Model, llm.Temperature, llm.TimeoutMs, llm.OnError, llm.PreserveText, llm.MaxTokens));

        page.Llm.MaxTokens = null;
        Assert.Null(Llm(session).MaxTokens);
    }

    [Fact]
    public void AnEmptiedTemperatureSavesNothingItPassedThrough()
    {
        var (page, session) = Open();

        page.Llm.Temperature = 1.5m;
        page.Llm.Temperature = null;
        session.Flush();

        Assert.Equal(0.2, ((LlmProcessorConfig)Store.Load().Processors["cleanup"]).Temperature);
        Assert.Equal(Strings.SettingsNumberRequired, page.Llm.TemperatureError);
    }

    [Fact]
    public void APromptEditIsWrittenAfterTheDelay()
    {
        var (page, _) = Open();

        page.Llm.Prompt.Text = "Fix {{text}}";
        Assert.False(_prompts.HasFile("cleanup"));

        _timers.Fire();
        Assert.Equal("Fix {{text}}", _prompts.Read("cleanup"));
        Assert.Equal("Your version in prompts/cleanup.md replaces the built-in prompt.", page.Llm.Prompt.Note);
        Assert.True(page.Llm.Prompt.CanRestore);
    }

    [Fact]
    public async Task ThePreviewUsesTheEditedPrompt()
    {
        var (page, _) = Open();
        string? promptAtRun = null;
        _runner.OnRun = () => promptAtRun = _prompts.Read("cleanup");

        page.Llm.Prompt.Text = "X {{text}}";
        await page.Llm.Preview.RunCommand.ExecuteAsync(null);

        Assert.Equal("X {{text}}", promptAtRun);
    }

    [Fact]
    public void ThePreviewRunsTheSampleAfterTheDelay()
    {
        var (page, session) = Open();
        _runner.Answer = _ => new RunResult("So I want you to check this code.", 812, null);

        page.Llm.Preview.SampleLanguage = page.Llm.Preview.Languages.Single(l => l.Value == "en");
        _timers.Fire();

        var run = _runner.Runs.Last();
        Assert.Equal(LlmPreview.Samples["en"], run.Text);
        Assert.Equal("en", run.Context.Language);
        Assert.Equal("cleanup", run.Steps.Single().Id);
        Assert.Same(Llm(session), run.Steps.Single().Config);
        Assert.Equal(("So I want you to check this code.", "812 ms", (string?)null), (page.Llm.Preview.Text, page.Llm.Preview.Latency, page.Llm.Preview.Error));
    }

    [Fact]
    public async Task ANewerPreviewWins()
    {
        var (page, _) = Open();
        var first = new TaskCompletionSource<RunResult>();
        _runner.Held.Enqueue(first);

        var older = page.Llm.Preview.RunCommand.ExecuteAsync(null);
        _runner.Answer = _ => new RunResult("newer", 5, null);
        page.Llm.Preview.SampleText = "інший текст";
        await page.Llm.Preview.RunCommand.ExecuteAsync(null);
        first.SetResult(new RunResult("older", 900, null));
        await older;

        Assert.Equal("newer", page.Llm.Preview.Text);
    }

    [Fact]
    public async Task APreviewFailureIsShown()
    {
        var (page, _) = Open();
        _runner.Answer = text => new RunResult(text, 7, "Connection refused (127.0.0.1:11434)");

        await page.Llm.Preview.RunCommand.ExecuteAsync(null);

        Assert.Equal("Connection refused (127.0.0.1:11434)", page.Llm.Preview.Error);
    }

    [Fact]
    public void RestoringTheBuiltInPromptDeletesYourVersion()
    {
        var (page, _) = Open();
        page.Llm.Prompt.Text = "Fix {{text}}";
        _timers.Fire();

        page.Llm.Prompt.RestoreCommand.Execute(null);

        Assert.False(_prompts.HasFile("cleanup"));
        Assert.NotEqual("Fix {{text}}", page.Llm.Prompt.Text);
        Assert.False(page.Llm.Prompt.CanRestore);
        _timers.Fire();
        Assert.False(_prompts.HasFile("cleanup"));
    }

    [Fact]
    public void ANewPromptCopiesTheTextAndIsChosen()
    {
        var (page, session) = Open();
        page.Llm.Prompt.Text = "Notes from {{text}}";

        page.Llm.Prompt.NewName = "notes";
        page.Llm.Prompt.CreateCommand.Execute(null);

        Assert.Equal("Notes from {{text}}", _prompts.Read("notes"));
        Assert.Equal("notes", Llm(session).Prompt);
        Assert.Contains("notes", page.Llm.Prompt.Names);
        Assert.Equal("", page.Llm.Prompt.NewName);
        Assert.False(_prompts.HasFile("cleanup"));
    }

    [Theory]
    [InlineData("a/b", "SettingsLlmPromptNameInvalid")]
    [InlineData("email", "SettingsLlmPromptNameTaken")]
    [InlineData("Cleanup", "SettingsLlmPromptNameTaken")] // The same file on Windows.
    public void ABadPromptNameIsRefused(string name, string error)
    {
        var (page, session) = Open();

        page.Llm.Prompt.NewName = name;
        page.Llm.Prompt.CreateCommand.Execute(null);

        Assert.Equal(Strings.ResourceManager.GetString(error, Strings.Culture), page.Llm.Prompt.NewNameError);
        Assert.Equal("cleanup", Llm(session).Prompt);
    }

    [Fact]
    public async Task TheKeyGoesToTheStore()
    {
        var (page, session) = Open();

        page.Llm.Key.Text = "sk-llm";
        await page.Llm.Key.CommitCommand.ExecuteAsync(null);

        Assert.Equal("sk-llm", _secrets.Values["dictum-processor-cleanup"]);
        Assert.Equal("secret:dictum-processor-cleanup", Llm(session).ApiKey);
        Assert.Equal("", page.Llm.Key.Text);
    }

    [Fact]
    public async Task ANewKeyOverTheOneInitStoredReplacesInitsEntry()
    {
        _secrets.Values["dictum-cleanup"] = "sk-old";
        var (page, session) = Open(cleanupKey: "secret:dictum-cleanup");

        page.Llm.Key.Text = "sk-new";
        await page.Llm.Key.CommitCommand.ExecuteAsync(null);

        Assert.Equal("secret:dictum-processor-cleanup", Llm(session).ApiKey);
        Assert.Equal(new Dictionary<string, string> { ["dictum-processor-cleanup"] = "sk-new" }, _secrets.Values);
    }

    [Fact]
    public async Task RemovingTheKeyInitStoredDeletesInitsEntry()
    {
        _secrets.Values["dictum-cleanup"] = "sk-old";
        var (page, session) = Open(cleanupKey: "secret:dictum-cleanup");

        await page.Llm.Key.RemoveCommand.ExecuteAsync(null);

        Assert.Null(Llm(session).ApiKey);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public void AddedProcessorsGetFreeIdsAndValidDefaults()
    {
        var (page, session) = Open();

        page.AddLlmCommand.Execute(null);
        Assert.Equal("llm", page.Selected?.Id);
        var added = Llm(session, "llm");
        Assert.Equal(("http://localhost:11434/v1", "qwen2.5:7b", "llm"), (added.BaseUrl, added.Model, added.Prompt));

        page.AddRulesCommand.Execute(null);
        Assert.Equal("rules", page.Selected?.Id);
        Assert.True(page.IsRules);
        Assert.False(session.HasErrors);
    }

    [Fact]
    public void OnlyAnUnusedProcessorCanBeRemoved()
    {
        var (page, session) = Open();

        page.Selected = page.Processors.Single(p => p.Id == "filler");
        Assert.True(page.CanRemove);
        page.RemoveCommand.Execute(null);

        Assert.False(session.Config.Processors.ContainsKey("filler"));
        Assert.Equal("cleanup", page.Selected?.Id);
    }

    [Fact]
    public void LeavingThePageWritesAPendingPrompt()
    {
        var (page, _) = Open();
        page.Llm.Prompt.Text = "Fix {{text}}";

        page.OnHidden();

        Assert.Equal("Fix {{text}}", _prompts.Read("cleanup"));
        Assert.Equal(0, _timers.Pending);
    }

    [Fact]
    public void OpeningSettingsOnAnotherPageSendsNothingToTheLlm()
    {
        var (page, _) = Open(shown: false);

        _timers.Fire();
        Assert.Empty(_runner.Runs);

        page.OnShown();
        _timers.Fire();
        Assert.Equal("cleanup", _runner.Runs.Single().Steps.Single().Id);
    }

    [Fact]
    public void ChoosingARulesProcessorStopsTheLlmPreview()
    {
        var (page, _) = Open();
        _timers.Fire();
        _runner.Runs.Clear();
        page.Llm.Preview.SampleText = "інший текст";

        page.Selected = page.Processors.Single(p => p.Id == "terms");
        _timers.Fire();
        Assert.Empty(_runner.Runs);

        page.Selected = page.Processors.Single(p => p.Id == "cleanup");
        _timers.Fire();
        Assert.Single(_runner.Runs);
    }

    [Fact]
    public async Task LeavingThePageDropsARunningPreview()
    {
        var (page, _) = Open();
        var held = new TaskCompletionSource<RunResult>();
        _runner.Held.Enqueue(held);
        var running = page.Llm.Preview.RunCommand.ExecuteAsync(null);
        Assert.True(page.Llm.Preview.Running);

        page.OnHidden();
        held.SetResult(new RunResult("late", 900, null));
        await running;

        Assert.Null(page.Llm.Preview.Text);
        Assert.False(page.Llm.Preview.Running);
    }

    [Fact]
    public void ANewLlmProcessorGetsItsOwnPrompt()
    {
        var (page, _) = Open();
        var cleanup = _prompts.Read("cleanup");

        page.AddLlmCommand.Execute(null);
        Assert.Equal(cleanup, _prompts.Read("llm"));
        Assert.Equal("llm", page.Llm.Prompt.Selected);
        Assert.Null(page.Llm.Prompt.Shared);

        page.Llm.Prompt.Text = "Mine {{text}}";
        _timers.Fire();
        Assert.Equal("Mine {{text}}", _prompts.Read("llm"));
        Assert.False(_prompts.HasFile("cleanup"));
    }

    // "LLM.md" is the same file as "llm.md" on Windows.
    [Fact]
    public void ANewLlmProcessorNeverTakesAnExistingPromptName()
    {
        var (page, session) = Open();
        _prompts.Write("LLM", "theirs {{text}}");

        page.AddLlmCommand.Execute(null);

        Assert.Equal("llm-2", Llm(session, "llm").Prompt);
        Assert.Equal("theirs {{text}}", _prompts.Read("LLM"));
    }

    [Fact]
    public void ASharedPromptSaysWhoElseUsesIt()
    {
        var (page, session) = Open();
        session.Edit(c =>
        {
            c.Processors["summary"] = Llm(session) with { Prompt = "cleanup" };
            return c;
        });

        Assert.Equal(string.Format(Strings.Culture, CompositeFormat.Parse(Strings.SettingsLlmPromptShared), "summary"), page.Llm.Prompt.Shared);
    }

    // "+ LLM prompt" copies the first processor's key reference.
    [Fact]
    public async Task ACopiedKeyOutlivesTheOriginal()
    {
        var (page, session) = Open();
        page.Llm.Key.Text = "sk-llm";
        await page.Llm.Key.CommitCommand.ExecuteAsync(null);
        page.AddLlmCommand.Execute(null);
        Assert.Equal("secret:dictum-processor-cleanup", Llm(session, "llm").ApiKey);

        page.Selected = page.Processors.Single(p => p.Id == "cleanup");
        await page.Llm.Key.RemoveCommand.ExecuteAsync(null);

        Assert.Null(Llm(session).ApiKey);
        Assert.Equal("sk-llm", _secrets.Values["dictum-processor-cleanup"]);
    }

    // A keyring prompt on Linux can answer after another processor was selected.
    [Fact]
    public async Task AKeyStoredLateGoesToTheProcessorItWasTypedFor()
    {
        var (page, session) = Open();
        page.AddLlmCommand.Execute(null);
        var before = Llm(session, "llm").ApiKey;
        page.Selected = page.Processors.Single(p => p.Id == "cleanup");
        var held = new TaskCompletionSource();
        _secrets.Held = held;

        page.Llm.Key.Text = "sk-cleanup";
        var committing = page.Llm.Key.CommitCommand.ExecuteAsync(null);
        page.Selected = page.Processors.Single(p => p.Id == "llm");
        held.SetResult();
        await committing;

        Assert.Equal("secret:dictum-processor-cleanup", Llm(session).ApiKey);
        Assert.Equal(before, Llm(session, "llm").ApiKey);
    }

    // Another program holds the prompt file open.
    [Fact]
    public void AnUnreadablePromptFileShowsWhyInsteadOfCrashing()
    {
        var (page, _) = Open();
        _prompts.Write("cleanup", "Mine {{text}}");
        using (File.Open(Path.Combine(_dir.Path, "prompts", "cleanup.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            page.Selected = page.Processors.Single(p => p.Id == "terms");
            page.Selected = page.Processors.Single(p => p.Id == "cleanup");
        }

        Assert.NotNull(page.Llm.Prompt.Error);
        Assert.Equal("Mine {{text}}", _prompts.Read("cleanup"));
    }

    [Fact]
    public void RestoringALockedPromptShowsWhyInsteadOfCrashing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file that is open");
        var (page, _) = Open();
        page.Llm.Prompt.Text = "Fix {{text}}";
        _timers.Fire();

        using (File.Open(Path.Combine(_dir.Path, "prompts", "cleanup.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            page.Llm.Prompt.RestoreCommand.Execute(null);
        }

        Assert.NotNull(page.Llm.Prompt.Error);
        Assert.Equal("Fix {{text}}", _prompts.Read("cleanup"));
        Assert.Equal("Fix {{text}}", page.Llm.Prompt.Text);
    }
}
