using System.Net;
using System.Net.Http.Headers;

namespace Dictum.Core.Http;

/// <summary>
/// What every OpenAI-compatible server shares (llama-server, llama-swap, Ollama, OpenAI, Groq): endpoints under the base
/// URL, a Bearer key, and <c>GET /models</c>, which a health check asks for.
/// </summary>
public static class OpenAiCompatible
{
    public const string ModelsPath = "models";

    private const string BearerScheme = "Bearer";

    /// <summary>How much of an error body a message quotes.</summary>
    private const int SnippetLength = 300;

    /// <summary><c>{baseUrl}/{path}</c>, with or without a trailing slash on the base URL.</summary>
    public static Uri Endpoint(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path}");

    /// <summary>Sends <paramref name="key"/> as the Bearer token; no key, no header.</summary>
    public static void Authorize(HttpRequestMessage message, string? key)
    {
        if (key is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, key);
        }
    }

    /// <summary>The start of an answer's body, for an error message.</summary>
    public static string Snippet(string body) => body.Length <= SnippetLength ? body : body[..SnippetLength] + "…";

    /// <summary>401 or 403: the server turned the key down.</summary>
    public static bool RejectsKey(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary><c>GET {baseUrl}/models</c> with <paramref name="key"/>. The caller disposes the answer.</summary>
    public static async Task<HttpResponseMessage> GetModelsAsync(HttpClient http, string baseUrl, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(baseUrl, ModelsPath));
        Authorize(request, key);
        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }
}
