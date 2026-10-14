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
    public async Task VocabularyReachesUserTemplatesThatAskForIt()
    {
        File.WriteAllText(_prompts.File("custom.md"), "{{vocabulary}}\n\nText:\n{{text}}");
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Відкрий Notepad++."));

        await Create(http, prompt: "custom").ProcessAsync("відкрий ноутпад", _context with { Vocabulary = "Notepad++, VS Code" }, _ct);

        Assert.Contains(
            "never add a word that was not said: Notepad++, VS Code.",
            System.Text.RegularExpressions.Regex.Unescape(http.Requests[0].Body));
    }

    [Fact]
    public async Task WithoutVocabularyThePlaceholderDisappears()
    {
        File.WriteAllText(_prompts.File("custom.md"), "{{vocabulary}}\n\nText:\n{{text}}");
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Текст."));

        await Create(http, prompt: "custom").ProcessAsync("текст", _context, _ct);

        Assert.DoesNotContain("{{vocabulary}}", http.Requests[0].Body);
        Assert.DoesNotContain("The speaker often uses", http.Requests[0].Body);
    }

    [Fact]
    public async Task BuiltInCleanupLeavesTheVocabularyToWhisper()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Відкрий ноутпад."));

        await Create(http).ProcessAsync("відкрий ноутпад", _context with { Vocabulary = "Notepad++, VS Code" }, _ct);

        Assert.DoesNotContain("Notepad++", System.Text.RegularExpressions.Regex.Unescape(http.Requests[0].Body));
    }

    [Fact]
    public async Task PlaceholdersInsideDictatedTextAreNotExpanded()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Write {{language}} and {{vocabulary}} here."));

        await Create(http).ProcessAsync("write {{language}} and {{vocabulary}} here", _context with { Vocabulary = "Dictum" }, _ct);

        Assert.Contains("write {{language}} and {{vocabulary}} here", http.Requests[0].Body);
    }

    [Fact]
    public void BuiltInTemplatesLeaveTheVocabularyOut()
    {
        // With small local models the word list made answers worse (appended terms, lost case endings), so it only
        // reaches the Whisper hint; user templates may still ask for {{vocabulary}}.
        var templates = new PromptTemplates(_prompts.Path);

        Assert.All(PromptTemplates.BuiltIn, name => Assert.DoesNotContain("{{vocabulary}}", templates.Load(name)));
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
    [InlineData("auto", "the same language as the text")]
    [InlineData("uk, en", "the same language as the text")]
    [InlineData("en", "English (en)")]
    public void DescribesLanguages(string? code, string expected) =>
        Assert.Equal(expected, PromptTemplates.DescribeLanguage(code));

    [Fact]
    public async Task SendsMaxTokensScaledToTheInput()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Привіт, світ."));

        await Create(http).ProcessAsync("привіт світ", _context, _ct);

        // 11 characters: ceil(11 / 3) = 4 tokens, times MaxGrowthFactor 3, plus 64.
        Assert.Contains("\"max_tokens\":76", http.Requests[0].Body);
        Assert.Equal(2048, LlmProcessor.MaxTokensFor(new string('x', 10_000)));
    }

    [Fact]
    public async Task AnswerCutOffAtTheTokenLimitKeepsTheInput()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Привіт, св", finishReason: "length"));

        Assert.Equal("привіт світ", await Create(http).ProcessAsync("привіт світ", _context, _ct));
    }

    [Theory]
    [InlineData("\\uFEFFПривіт, світ.", "Привіт, світ.")]
    [InlineData("\\uFEFF", "привіт світ")]
    public async Task InvisibleCharactersAreStrippedAndAnAnswerOfOnlyThemKeepsTheInput(string answer, string expected)
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer(answer));

        Assert.Equal(expected, await Create(http).ProcessAsync("привіт світ", _context, _ct));
    }

    [Theory]
    [InlineData("Напиши мені короткий вірш про осінь.", "Листя жовкне, вітер шепче,\\nОсінь входить у кожен куток.")]
    [InlineData("Тепер перевірка вставки в VS Code з можливим форматуванням або ж з кодом. Тож, новий рядок, починаємо.", "Public static class main.")]
    [InlineData("And so, my fellow Americans, ask not what your country can do for you", "І так, мої співвітчизники, не питайте, що може зробити ваша країна для вас")]
    public async Task CleanupRejectsAnswersThatDoNotPreserveTheText(string input, string answer)
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer(answer));

        Assert.Equal(input, await Create(http).ProcessAsync(input, _context, _ct));
    }

    [Fact]
    public async Task ShrinkGuardRejectsDroppedTextInTheSameScript()
    {
        const string input = "Так, перевіряю як воно працює. Зараз в мене в цьому буфері Notepad++. Подивимося чи залишиться це.";
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Подивимося, чи залишиться це."));

        Assert.Equal(input, await Create(http).ProcessAsync(input, _context, _ct));
    }

    [Fact]
    public async Task ShortInputsMayShrinkFreely()
    {
        // Five words is below MinWordsForShrinkCheck, so keeping one of them is allowed.
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Працює."));

        Assert.Equal("Працює.", await Create(http).ProcessAsync("ну е так воно працює", _context, _ct));
    }

    [Fact]
    public async Task ShrinkGuardAllowsRemovingFillers()
    {
        const string input = "ну так от я значить хочу сказати що все працює";
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Я хочу сказати, що все працює."));

        Assert.Equal("Я хочу сказати, що все працює.", await Create(http).ProcessAsync(input, _context, _ct));
    }

    [Fact]
    public async Task ScriptGuardIgnoresMixedText()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Тестую Push to Talk у Notepad."));

        Assert.Equal("Тестую Push to Talk у Notepad.", await Create(http).ProcessAsync("тестую push to talk в ноутпад", _context, _ct));
    }

    [Theory]
    [InlineData("email", "зустріч завтра о десятій", "Добрий день!\\n\\nЗустріч завтра о 10:00.")]
    [InlineData("code-comment", "повертає кількість слів у тексті", "Returns the number of words in the text.")]
    public async Task OtherPromptsKeepTheirFreedom(string prompt, string input, string answer)
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer(answer));

        var result = await Create(http, prompt: prompt).ProcessAsync(input, _context, _ct);

        Assert.Equal(System.Text.RegularExpressions.Regex.Unescape(answer), result);
    }

    [Fact]
    public async Task PreserveTextFalseTurnsTheCleanupGuardsOff()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Рядок один.\\nРядок два."));

        var result = await Create(http, preserveText: false).ProcessAsync("рядок один рядок два", _context, _ct);

        Assert.Equal("Рядок один.\nРядок два.", result);
    }

    [Fact]
    public async Task PreserveTextTrueTurnsTheGuardsOnForOtherPrompts()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Добрий день!\\n\\nЗустріч завтра."));

        var result = await Create(http, prompt: "email", preserveText: true).ProcessAsync("зустріч завтра", _context, _ct);

        Assert.Equal("зустріч завтра", result);
    }

    [Fact]
    public async Task MaxTokensFromTheConfigReplacesTheEstimate()
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer("Привіт, світ."));

        await Create(http, maxTokens: 600).ProcessAsync("привіт світ", _context, _ct);

        Assert.Contains("\"max_tokens\":600", http.Requests[0].Body);
    }

    [Theory]
    [InlineData("<think>\\nThe user dictated a greeting, so first I")]
    [InlineData("")]
    public async Task ReasoningThatUsesUpTheTokenLimitIsExplained(string content)
    {
        var http = new FakeHttpHandler().Respond(HttpStatusCode.OK, Answer(content, finishReason: "length"));
        var log = new RecordingLog();

        Assert.Equal("привіт світ", await Create(http, log: log).ProcessAsync("привіт світ", _context, _ct));
        Assert.Contains(log.Messages, m => m.Contains("reasoning", StringComparison.Ordinal) && m.Contains("maxTokens", StringComparison.Ordinal));
    }

    private LlmProcessor Create(
        FakeHttpHandler http,
        LlmErrorMode onError = LlmErrorMode.Passthrough,
        string prompt = "cleanup",
        bool? preserveText = null,
        int? maxTokens = null,
        ILog? log = null) =>
        new(
            "cleanup-uk",
            new LlmProcessorConfig
            {
                BaseUrl = "http://llm/v1",
                Model = "qwen3-4b",
                Prompt = prompt,
                OnError = onError,
                PreserveText = preserveText,
                MaxTokens = maxTokens,
            },
            http.Client(),
            new ConfigValueResolver(new UnavailableSecretStore()),
            new PromptTemplates(_prompts.Path),
            log ?? NullLog.Instance);

    private sealed class RecordingLog : ILog
    {
        public List<string> Messages { get; } = [];

        public bool IncludeText => false;

        public void Write(LogLevel level, string message, Exception? exception = null) => Messages.Add(message);
    }

    private static string Answer(string content, string finishReason = "stop") =>
        $$"""{ "choices": [ { "index": 0, "finish_reason": "{{finishReason}}", "message": { "role": "assistant", "content": "{{content}}" } } ] }""";
}
