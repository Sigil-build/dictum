using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Core.Text;

namespace Dictum.Processing;

/// <summary>
/// Rewrites text through any OpenAI-compatible <c>/chat/completions</c> endpoint
/// (llama-server, llama-swap, Ollama, cloud APIs).
/// </summary>
public sealed partial class LlmProcessor(
    string id,
    LlmProcessorConfig config,
    HttpClient http,
    ConfigValueResolver values,
    PromptTemplates templates,
    ILog log) : ITextProcessor, IHealthCheck
{
    /// <summary>Longer answers usually mean the model replied to the dictation instead of fixing it.</summary>
    public const int MaxGrowthFactor = 3;

    /// <summary>The shrink guard needs at least this many input words; short phrases change a lot legitimately.</summary>
    public const int MinWordsForShrinkCheck = 6;

    /// <summary>An answer with fewer than a third of the input's words dropped part of the dictation.</summary>
    private const int MaxShrinkFactor = 3;

    /// <summary>Upper bound of the token limit scaled to the input.</summary>
    private const int MaxScaledTokens = 2048;

    private const double CharsPerToken = 3.0;

    /// <summary>Tokens added to the scaled limit so very short inputs still get room for an answer.</summary>
    private const int TokenHeadroom = 64;

    /// <summary>The <c>finish_reason</c> of an answer cut off at <c>max_tokens</c>.</summary>
    private const string FinishReasonLength = "length";

    private const int SnippetLength = 300;

    public string Id => id;

    private bool PreservesText => config.PreserveText ?? config.Prompt == LlmProcessorConfig.CleanupPrompt;

    public async Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct)
    {
        try
        {
            var prompt = templates.Render(config.Prompt, text, context);
            var (content, finishReason) = await CompleteAsync(prompt, config.MaxTokens ?? MaxTokensFor(text), ct).ConfigureAwait(false);
            var answer = TextChecks.StripInvisible(StripThinking(content)).Trim();
            if (Rejection(text, answer, finishReason) is { } reason)
            {
                log.Warn($"Processor '{id}' answer {reason}; keeping the input text");
                return text;
            }

            return answer;
        }
        catch (Exception ex) when (config.OnError == LlmErrorMode.Passthrough && ex is ProviderUnavailableException)
        {
            log.Warn($"Processor '{id}' failed, keeping the input text: {ex.Message}");
            return text;
        }
    }

    /// <summary>Room for an answer up to <see cref="MaxGrowthFactor"/> times the input, at about 3 characters per token.</summary>
    public static int MaxTokensFor(string input) =>
        Math.Min(MaxScaledTokens, (int)Math.Ceiling(input.Length / CharsPerToken) * MaxGrowthFactor + TokenHeadroom);

    /// <summary>Why the answer cannot replace the input, or null when it can.</summary>
    private string? Rejection(string input, string answer, string? finishReason)
    {
        if (finishReason == FinishReasonLength)
        {
            // An unclosed <think> block, or no content at all (llama-server moves reasoning to reasoning_content),
            // means the model spent the whole budget thinking.
            return answer.Length == 0 || answer.Contains("<think>", StringComparison.Ordinal)
                ? $"was cut off at the token limit while the model was still reasoning; turn reasoning off on the server, or raise processors.{id}.maxTokens"
                : "was cut off at the token limit";
        }

        if (answer.Length == 0)
        {
            return "is empty";
        }

        if (answer.Length > input.Length * MaxGrowthFactor)
        {
            return $"is {answer.Length} chars for {input.Length} of input";
        }

        if (!PreservesText)
        {
            return null;
        }

        if (TextChecks.HasLineBreak(answer) && !TextChecks.HasLineBreak(input))
        {
            return "added line breaks";
        }

        var inputWords = TextChecks.WordCount(input);
        var answerWords = TextChecks.WordCount(answer);
        if (inputWords >= MinWordsForShrinkCheck && answerWords * MaxShrinkFactor < inputWords)
        {
            return $"kept {answerWords} of {inputWords} words";
        }

        return TextChecks.IsScriptFlip(input, answer) ? "is in another script" : null;
    }

    public async Task<HealthStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            _ = templates.Load(config.Prompt);
            using var message = new HttpRequestMessage(HttpMethod.Get, Endpoint("models"));
            await AuthorizeAsync(message, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(IHealthCheck.ServerTimeout);
            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    HealthStatus.Error($"{config.BaseUrl} rejected the API key", "Check the apiKey reference in the config."),
                _ when response.IsSuccessStatusCode => HealthStatus.Ok($"{config.BaseUrl} reachable, model {config.Model}, prompt '{config.Prompt}'"),
                _ => HealthStatus.Warning($"{config.BaseUrl}/models answered {(int)response.StatusCode}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ConfigException)
        {
            return HealthStatus.Error($"{config.BaseUrl}: {ex.Message}", "Is the LLM server running, and is baseUrl correct (including /v1)?");
        }
    }

    private async Task<(string Content, string? FinishReason)> CompleteAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.TimeoutMs);

        var request = new ChatRequest(config.Model, [new ChatMessage("user", prompt)], config.Temperature, Stream: false, maxTokens);
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint("chat/completions"))
        {
            Content = JsonContent.Create(request, LlmJsonContext.Default.ChatRequest),
        };
        await AuthorizeAsync(message, ct).ConfigureAwait(false);

        try
        {
            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderUnavailableException($"LLM '{id}' answered {(int)response.StatusCode}: {Snippet(body)}");
            }

            var parsed = JsonSerializer.Deserialize(body, LlmJsonContext.Default.ChatResponse);
            return parsed?.Choices is [{ Message.Content: { } content } choice, ..]
                ? (content, choice.FinishReason)
                : throw new ProviderUnavailableException($"LLM '{id}' returned no choices: {Snippet(body)}");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException($"LLM '{id}' did not answer within {config.TimeoutMs} ms.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException($"LLM '{id}' is unreachable at {config.BaseUrl}: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ProviderUnavailableException($"LLM '{id}' returned invalid JSON.", ex);
        }
    }

    private async Task AuthorizeAsync(HttpRequestMessage message, CancellationToken ct)
    {
        if (await values.ResolveAsync(config.ApiKey, ct).ConfigureAwait(false) is { } key)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private Uri Endpoint(string path) => new($"{config.BaseUrl.TrimEnd('/')}/{path}");

    private static string Snippet(string body) => body.Length <= SnippetLength ? body : body[..SnippetLength] + "…";

    /// <summary>Reasoning models (e.g. Qwen3) may prepend a &lt;think&gt; block.</summary>
    private static string StripThinking(string answer) => ThinkBlock().Replace(answer, string.Empty);

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();
}

internal sealed record ChatMessage(string Role, string Content);

internal sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    double Temperature,
    bool Stream,
    [property: JsonPropertyName("max_tokens")] int MaxTokens);

internal sealed record ChatChoice(ChatMessage? Message, [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

internal sealed record ChatResponse(IReadOnlyList<ChatChoice>? Choices);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;
