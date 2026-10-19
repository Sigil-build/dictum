using System.Globalization;
using System.Text.Json;
using Dictum.App.Resources;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;

namespace Dictum.App.Settings;

/// <summary>The answer to the setup wizard's "Test connection": whether Next may go on, and what to show.</summary>
public sealed record ServerCheck(bool Ok, string Message);

public interface IServerProbe
{
    /// <param name="apiKey">A typed key, a reference (<c>env:VAR</c>, <c>secret:name</c>) or null.</param>
    Task<ServerCheck> CheckAsync(string baseUrl, string model, string? apiKey, CancellationToken ct);
}

/// <summary>
/// Tests an OpenAI-compatible server as clean-up will use it: <c>GET {url}/models</c> with the key, timed, within
/// <see cref="IHealthCheck.ServerTimeout"/> unless the caller gives another limit.
/// When the server lists several models, the chosen one must be there, so a typo does not make clean-up fail later in
/// silence. A server that lists none (some proxies) or one (llama-server serves its model under any name) passes, and
/// an Ollama name without its tag matches the <c>:latest</c> one.
/// </summary>
public sealed class ServerProbe(HttpClient http, ISecretStore secrets, TimeProvider time, TimeSpan? timeout = null) : IServerProbe
{
    /// <summary>The tag Ollama gives a model pulled without one.</summary>
    private const string OllamaDefaultTag = ":latest";

    /// <summary>How many of the server's models the "no such model" message names.</summary>
    private const int ModelsNamed = 8;

    private const string DataProperty = "data";
    private const string IdProperty = "id";

    public async Task<ServerCheck> CheckAsync(string baseUrl, string model, string? apiKey, CancellationToken ct)
    {
        var url = baseUrl.Trim();
        var name = model.Trim();
        if (!ConfigValidator.IsHttpUrl(url))
        {
            return new ServerCheck(false, Strings.SetupServerBadUrl);
        }

        if (name.Length == 0)
        {
            return new ServerCheck(false, Strings.SetupServerNoModelName);
        }

        string? key;
        try
        {
            key = await new ConfigValueResolver(secrets).ResolveAsync(apiKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing secret is a ConfigException; Credential Manager and the portal throw their own.
            return new ServerCheck(false, Format(Strings.SetupServerKeyUnreadable, ex.Message));
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? IHealthCheck.ServerTimeout);
        var started = time.GetTimestamp();
        try
        {
            using var response = await OpenAiCompatible.GetModelsAsync(http, url, string.IsNullOrEmpty(key) ? null : key, limit.Token);
            var milliseconds = (long)time.GetElapsedTime(started).TotalMilliseconds;
            if (OpenAiCompatible.RejectsKey(response.StatusCode))
            {
                return new ServerCheck(false, Strings.SetupServerKeyRejected);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ServerCheck(false, Format(Strings.SetupServerAnswered, (int)response.StatusCode));
            }

            var models = await ModelIdsAsync(response, limit.Token);
            return models.Count > 1 && !models.Contains(name, StringComparer.Ordinal) && !models.Contains(name + OllamaDefaultTag, StringComparer.Ordinal)
                ? new ServerCheck(false, Format(Strings.SetupServerNoModel, name, string.Join(", ", models.Take(ModelsNamed))))
                : new ServerCheck(true, Format(Strings.SetupServerReachable, milliseconds));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ServerCheck(false, Format(Strings.SetupServerTimeout, url));
        }
        catch (HttpRequestException ex)
        {
            return new ServerCheck(false, Format(Strings.SetupServerUnreachable, url, ex.Message));
        }
    }

    /// <summary>The ids in an OpenAI-style list (<c>{"data":[{"id":…}]}</c>); none when the body is something else.</summary>
    private static async Task<List<string>> ModelIdsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(DataProperty, out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. data.EnumerateArray()
                    .Select(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty(IdProperty, out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null)
                    .OfType<string>(),
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
