using System.Net;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Processing;

namespace Dictum.Core.Tests.Processing;

public class LlmProcessorTests : IDisposable
{
    private readonly TempDirectory _prompts = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly ProcessingContext _context = new("p", "uk", null);

    public void Dispose() => _prompts.Dispose();

    [Fact]
    public async Task SendsRenderedPromptAndReturnsTheAnswer()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Привіт, світ."));

        var result = await Create(http).ProcessAsync("привіт світ", _context, _ct);

        Assert.Equal("Привіт, світ.", result);
        var (request, body) = Assert.Single(http.Requests);
        Assert.Equal("http://llm/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Contains("\"model\":\"qwen3-4b\"", body);
        Assert.Contains("\"temperature\":0.2", body);
        Assert.Contains("\"stream\":false", body);
        Assert.Contains("привіт світ", System.Text.RegularExpressions.Regex.Unescape(body));
        Assert.Contains("Ukrainian (uk)", body);
    }

    [Fact]
    public async Task StripsThinkingBlocks()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("<think>\\nlet me see\\n</think>\\n\\nFixed text."));

        Assert.Equal("Fixed text.", await Create(http).ProcessAsync("fixed text", _context, _ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sure! Here is a long answer that goes far beyond what was dictated, with extra commentary.")]
    public async Task GuardKeepsInputForEmptyOrRunawayAnswers(string answer)
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer(answer));

        Assert.Equal("short text", await Create(http).ProcessAsync("short text", _context, _ct));
    }

    [Fact]
    public async Task PassthroughModeKeepsInputOnFailure()
    {
        var http = new FakeHttpHandler().Throw(new HttpRequestException("refused"));

        Assert.Equal("keep me", await Create(http).ProcessAsync("keep me", _context, _ct));
    }

    [Fact]
    public async Task FailModeThrows()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Create(http, onError: LlmErrorMode.Fail).ProcessAsync("text", _context, _ct));
    }

    [Fact]
    public async Task UserTemplateOverridesBuiltIn()
    {
        File.WriteAllText(_prompts.File("cleanup.md"), "CUSTOM {{language}} / {{app}} / {{text}}");
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("ok text"));

        await Create(http).ProcessAsync("some text", _context with { ActiveApp = "code" }, _ct);

        Assert.Contains("CUSTOM Ukrainian (uk) / code / some text", http.Requests[0].Body);
    }

    [Fact]
    public async Task VocabularyReachesThePrompt()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Відкрий Notepad++."));

        await Create(http).ProcessAsync("відкрий ноутпад", _context with { Vocabulary = "Notepad++, VS Code" }, _ct);

        Assert.Contains(
            "never add a word that was not said: Notepad++, VS Code.",
            System.Text.RegularExpressions.Regex.Unescape(http.Requests[0].Body));
    }

    [Fact]
    public async Task WithoutVocabularyThePlaceholderDisappears()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Текст."));

        await Create(http).ProcessAsync("текст", _context, _ct);

        Assert.DoesNotContain("{{vocabulary}}", http.Requests[0].Body);
        Assert.DoesNotContain("The speaker often uses", http.Requests[0].Body);
    }

    [Fact]
    public async Task PlaceholdersInsideDictatedTextAreNotExpanded()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Write {{language}} and {{vocabulary}} here."));

        await Create(http).ProcessAsync("write {{language}} and {{vocabulary}} here", _context with { Vocabulary = "Dictum" }, _ct);

        Assert.Contains("write {{language}} and {{vocabulary}} here", http.Requests[0].Body);
    }

    [Fact]
    public void EveryBuiltInTemplateHasTheVocabularyPlaceholder()
    {
        var templates = new PromptTemplates(_prompts.Path);

        Assert.All(PromptTemplates.BuiltIn, name => Assert.Contains("{{vocabulary}}", templates.Load(name)));
    }

    [Fact]
    public void FindsUserTemplatesWithoutTheVocabularyPlaceholder()
    {
        File.WriteAllText(_prompts.File("old.md"), "Fix: {{text}}");
        File.WriteAllText(_prompts.File("new.md"), "{{vocabulary}} Fix: {{text}}");

        Assert.True(PromptTemplates.UserTemplateLacksVocabulary(_prompts.Path, "old"));
        Assert.False(PromptTemplates.UserTemplateLacksVocabulary(_prompts.Path, "new"));
        Assert.False(PromptTemplates.UserTemplateLacksVocabulary(_prompts.Path, "cleanup"));
    }

    [Fact]
    public void BuiltInTemplatesAreEmbedded() =>
        Assert.Equal(["cleanup", "code-comment", "email"], PromptTemplates.BuiltIn);

    [Fact]
    public void UnknownTemplateIsAConfigError()
    {
        var templates = new PromptTemplates(_prompts.Path);

        var ex = Assert.Throws<ConfigException>(() => templates.Load("nope"));

        Assert.Contains("cleanup", ex.Message);
    }

    [Theory]
    [InlineData(null, "the same language as the text")]
    [InlineData("en", "English (en)")]
    public void DescribesLanguages(string? code, string expected) =>
        Assert.Equal(expected, PromptTemplates.DescribeLanguage(code));

    private LlmProcessor Create(FakeHttpHandler http, LlmErrorMode onError = LlmErrorMode.Passthrough) =>
        new(
            "cleanup-uk",
            new LlmProcessorConfig { BaseUrl = "http://llm/v1", Model = "qwen3-4b", Prompt = "cleanup", OnError = onError },
            http.Client(),
            new ConfigValueResolver(new UnavailableSecretStore()),
            new PromptTemplates(_prompts.Path),
            NullLog.Instance);

    private static string Answer(string content) =>
        $$"""{ "choices": [ { "index": 0, "message": { "role": "assistant", "content": "{{content}}" } } ] }""";
}
