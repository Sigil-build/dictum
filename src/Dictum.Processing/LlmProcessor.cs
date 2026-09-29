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

    public string Id => id;

    public async Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct)
    {
        try
        {
            var prompt = templates.Render(config.Prompt, text, context);
            var answer = StripThinking(await CompleteAsync(prompt, ct).ConfigureAwait(false)).Trim();

            if (answer.Length == 0)
            {
                log.Warn($"Processor '{id}' returned an empty answer; keeping the input text");
                return text;
            }

            if (answer.Length > text.Length * MaxGrowthFactor)
            {
                log.Warn($"Processor '{id}' answer is {answer.Length} chars for {text.Length} of input; keeping the input text");
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

    public async Task<HealthStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            _ = templates.Load(config.Prompt);
            using var message = new HttpRequestMessage(HttpMethod.Get, Endpoint("models"));
            await AuthorizeAsync(message, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
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

    private async Task<string> CompleteAsync(string prompt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.TimeoutMs);

        var request = new ChatRequest(config.Model, [new ChatMessage("user", prompt)], config.Temperature, Stream: false);
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
            return parsed?.Choices is [{ Message.Content: { } content }, ..]
                ? content
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

    private static string Snippet(string body) => body.Length <= 300 ? body : body[..300] + "…";

    /// <summary>Reasoning models (e.g. Qwen3) may prepend a &lt;think&gt; block.</summary>
    private static string StripThinking(string answer) => ThinkBlock().Replace(answer, string.Empty);

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();
}

internal sealed record ChatMessage(string Role, string Content);

internal sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, double Temperature, bool Stream);

internal sealed record ChatChoice(ChatMessage? Message);

internal sealed record ChatResponse(IReadOnlyList<ChatChoice>? Choices);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;
