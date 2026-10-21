using System.Net;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Core.Http;

/// <summary>A server's answer: its status and body. The provider judges the status in its own words.</summary>
public sealed record ServerAnswer(HttpStatusCode Status, string? ReasonPhrase, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and <= 299;
}

/// <summary>
/// One provider's OpenAI-compatible server: its endpoints, the key its config names, the health probe, and the
/// <see cref="ProviderUnavailableException"/> for a server that does not answer.
/// </summary>
/// <param name="name">How messages name the provider, e.g. <c>LLM 'cleanup'</c>.</param>
/// <param name="apiKey">A literal key, <c>env:VAR</c>, <c>secret:name</c>, or null for none.</param>
public sealed class OpenAiCompatibleServer(HttpClient http, ConfigValueResolver values, string name, string baseUrl, string? apiKey)
{
    public Uri Endpoint(string path) => OpenAiCompatible.Endpoint(baseUrl, path);

    /// <summary>How to give the provider its key, when its <c>secret:</c> or <c>env:</c> reference cannot be read.</summary>
    /// <param name="error">Why the key cannot be read.</param>
    /// <param name="secretName">The secret Settings keeps this provider's key under.</param>
    /// <param name="settings">Where in Settings the key can be typed, e.g. <c>Settings → Processors</c>.</param>
    public string KeyHint(ConfigException error, string secretName, string settings)
    {
        if (ConfigValueResolver.TryGetEnvVariable(apiKey ?? string.Empty, out var variable))
        {
            return $"The API key cannot be read: set the environment variable {variable} where Dictum starts, or type the key in {settings}.";
        }

        if (error is SecretStoreUnavailableException)
        {
            // Both 'dictum secret set' and Settings store the key there, so neither would help.
            return "The API key cannot be read: with no OS secret store, give it as 'env:VAR' in the config and set that variable where Dictum starts.";
        }

        var name = apiKey is { } key && key.StartsWith(ConfigValueResolver.SecretPrefix, StringComparison.Ordinal)
            ? key[ConfigValueResolver.SecretPrefix.Length..]
            : secretName;
        return $"The API key cannot be read: store it with 'dictum secret set {name}', or type it in {settings}.";
    }

    /// <summary><c>GET /models</c> with the key, within <see cref="IHealthCheck.ServerTimeout"/>. The caller disposes the answer.</summary>
    public async Task<HttpResponseMessage> ProbeModelsAsync(CancellationToken ct)
    {
        var key = await values.ResolveAsync(apiKey, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IHealthCheck.ServerTimeout);
        return await OpenAiCompatible.GetModelsAsync(http, baseUrl, key, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the request, adds the key and reads the answer. The <paramref name="timeoutMs"/> limit counts from the
    /// start, key lookup included. A server that does not answer in time or cannot be reached throws a
    /// <see cref="ProviderUnavailableException"/>; cancelling <paramref name="ct"/> throws as usual.
    /// </summary>
    public async Task<ServerAnswer> SendAsync(Func<HttpRequestMessage> request, int timeoutMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        using var message = request();
        OpenAiCompatible.Authorize(message, await values.ResolveAsync(apiKey, ct).ConfigureAwait(false));

        try
        {
            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new ServerAnswer(response.StatusCode, response.ReasonPhrase, body);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException($"{name} did not answer within {timeoutMs} ms.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException($"{name} is unreachable at {baseUrl}: {ex.Message}", ex);
        }
    }
}
