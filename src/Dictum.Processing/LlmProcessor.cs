using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;
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

    private const string ChatPath = "chat/completions";

    private readonly OpenAiCompatibleServer _server = new(http, values, $"LLM '{id}'", config.BaseUrl, config.ApiKey);

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
            HttpResponseMessage probe;
            try
            {
                probe = await _server.ProbeModelsAsync(ct).ConfigureAwait(false);
            }
            catch (ConfigException ex)
            {
                // The probe throws this only for the key: a secret: or env: reference that cannot be read.
                return HealthStatus.Error(ex.Message, KeyHint());
            }

            using var response = probe;
            return response.StatusCode switch
            {
                var status when OpenAiCompatible.RejectsKey(status) =>
                    HealthStatus.Error($"{config.BaseUrl} rejected the API key", "Check the apiKey reference in the config."),
                _ when response.IsSuccessStatusCode => HealthStatus.Ok($"{config.BaseUrl} reachable, model {config.Model}, prompt '{config.Prompt}'"),
                _ => HealthStatus.Warning($"{config.BaseUrl}/{OpenAiCompatible.ModelsPath} answered {(int)response.StatusCode}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ConfigException)
        {
            return HealthStatus.Error($"{config.BaseUrl}: {ex.Message}", "Is the LLM server running, and is baseUrl correct (including /v1)?");
        }
    }

    /// <summary>How to give the processor its key, for a <c>secret:</c> or <c>env:</c> reference that cannot be read.</summary>
    private string KeyHint() =>
        ConfigValueResolver.TryGetEnvVariable(config.ApiKey ?? string.Empty, out var variable)
            ? $"The API key cannot be read: set the environment variable {variable} where Dictum starts, or type the key in Settings → Processors."
            : $"The API key cannot be read: store it with 'dictum secret set {SecretName()}', or type it in Settings → Processors.";

    /// <summary>The secret the key reference names, or the name Settings stores this processor's key under.</summary>
    private string SecretName() =>
        config.ApiKey is { } key && key.StartsWith(ConfigValueResolver.SecretPrefix, StringComparison.Ordinal)
            ? key[ConfigValueResolver.SecretPrefix.Length..]
            : SecretNames.Processor(id);

    private async Task<(string Content, string? FinishReason)> CompleteAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        var request = new ChatRequest(config.Model, [new ChatMessage("user", prompt)], config.Temperature, Stream: false, maxTokens);
        ServerAnswer answer;
        try
        {
            answer = await _server.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, _server.Endpoint(ChatPath))
                {
                    Content = JsonContent.Create(request, LlmJsonContext.Default.ChatRequest),
                },
                config.TimeoutMs,
                ct).ConfigureAwait(false);
        }
        catch (ConfigException ex)
        {
            // Sending throws this only for the key. Like a server that does not answer, it follows onError.
            throw new ProviderUnavailableException($"LLM '{id}' cannot read its API key: {ex.Message}", ex);
        }

        if (!answer.IsSuccess)
        {
            throw new ProviderUnavailableException($"LLM '{id}' answered {(int)answer.Status}: {OpenAiCompatible.Snippet(answer.Body)}");
        }

        ChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(answer.Body, LlmJsonContext.Default.ChatResponse);
        }
        catch (JsonException ex)
        {
            throw new ProviderUnavailableException($"LLM '{id}' returned invalid JSON.", ex);
        }

        return parsed?.Choices is [{ Message.Content: { } content } choice, ..]
            ? (content, choice.FinishReason)
            : throw new ProviderUnavailableException($"LLM '{id}' returned no choices: {OpenAiCompatible.Snippet(answer.Body)}");
    }

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
