using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dictum.App.Resources;
using Dictum.Core.Configuration;

namespace Dictum.App.Settings;

/// <summary>The answer to "Test connection" (wizard step 3): whether Next may go on, and what to show.</summary>
public sealed record ServerCheck(bool Ok, string Message);

public interface IServerProbe
{
    /// <param name="apiKey">A typed key, a reference (<c>env:VAR</c>, <c>secret:name</c>) or null.</param>
    Task<ServerCheck> CheckAsync(string baseUrl, string model, string? apiKey, CancellationToken ct);
}

/// <summary>
/// Tests an OpenAI-compatible server as clean-up will use it (spec 8, step 3): <c>GET {url}/models</c> with the key,
/// within 5 s, timed. When the server lists its models, the chosen one must be there, so a typo does not make clean-up
/// fail later in silence; a server that lists none (some proxies) passes.
/// </summary>
public sealed class ServerProbe(HttpClient http, ISecretStore secrets, TimeProvider time, TimeSpan? timeout = null) : IServerProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

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
            // A missing secret is a ConfigException; Credential Manager and the portal throw their own (final review M2).
            return new ServerCheck(false, Format(Strings.SetupServerKeyUnreadable, ex.Message));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/models");
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        var started = time.GetTimestamp();
        try
        {
            using var response = await http.SendAsync(request, limit.Token);
            var milliseconds = (long)time.GetElapsedTime(started).TotalMilliseconds;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ServerCheck(false, Strings.SetupServerKeyRejected);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ServerCheck(false, Format(Strings.SetupServerAnswered, (int)response.StatusCode));
            }

            var models = await ModelIdsAsync(response, limit.Token);
            return models.Count > 0 && !models.Contains(name, StringComparer.Ordinal)
                ? new ServerCheck(false, Format(Strings.SetupServerNoModel, name, string.Join(", ", models.Take(8))))
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
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. data.EnumerateArray()
                    .Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(m => m.GetProperty("id").GetString()!),
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
