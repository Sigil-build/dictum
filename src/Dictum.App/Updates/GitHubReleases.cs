using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictum.App.Updates;

/// <summary>Dictum's newest release: its tag, such as <c>v0.5.0</c>, and its page on GitHub.</summary>
public sealed record LatestRelease(string Tag, Uri Page);

/// <summary>Why a check found no release.</summary>
public enum UpdateCheckFailure
{
    None,

    /// <summary>No connection: no network, no name resolution, a proxy or TLS failure.</summary>
    Unreachable,

    TimedOut,

    /// <summary>GitHub answers 60 requests an hour per address without a token.</summary>
    RateLimited,

    /// <summary>Another status, which <see cref="ReleaseLookup.StatusCode"/> gives.</summary>
    Answered,

    /// <summary>The answer named no release that Dictum can use.</summary>
    BadAnswer,
}

/// <summary>What a check got: the latest release, or why there is none.</summary>
/// <param name="StatusCode">GitHub's status when it answered with an error; 0 otherwise.</param>
/// <param name="Detail">What went wrong, in English, for the log.</param>
public sealed record ReleaseLookup(LatestRelease? Release, UpdateCheckFailure Failure = UpdateCheckFailure.None, int StatusCode = 0, string? Detail = null);

/// <summary>Where the update check asks for the newest release; a fake in tests.</summary>
public interface IReleaseSource
{
    /// <summary>Never throws for a failed request: the lookup says why. Throws only when <paramref name="ct"/> is cancelled.</summary>
    Task<ReleaseLookup> LatestAsync(CancellationToken ct);
}

/// <summary>
/// <c>GET https://api.github.com/repos/Sigil-build/dictum/releases/latest</c>, which skips drafts and pre-releases,
/// without a token, with <c>User-Agent: Dictum/&lt;version&gt;</c>. Only <c>tag_name</c> and <c>html_url</c> are read.
/// </summary>
public sealed class GitHubReleases(HttpClient http, string version, TimeSpan? timeout = null) : IReleaseSource
{
    public static readonly Uri Latest = new("https://api.github.com/repos/Sigil-build/dictum/releases/latest");

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Only Dictum's own release pages open in the browser.</summary>
    private const string PagePrefix = "https://github.com/Sigil-build/dictum/releases/";

    private const string MediaType = "application/vnd.github+json";

    /// <summary>The header GitHub sets to the requests left in the hour; 0 with a 403 is the rate limit.</summary>
    private const string RateLimitRemaining = "x-ratelimit-remaining";

    /// <summary>A client of its own: no default headers, the system's proxy, and no connection kept for a day.</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        ConnectTimeout = Timeout,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public async Task<ReleaseLookup> LatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Dictum", version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? Timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (IsRateLimit(response))
            {
                return new ReleaseLookup(null, UpdateCheckFailure.RateLimited, (int)response.StatusCode, "GitHub's rate limit");
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return new ReleaseLookup(null, UpdateCheckFailure.Answered, status, string.Create(CultureInfo.InvariantCulture, $"GitHub answered {status}"));
            }

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync(body, UpdateJsonContext.Default.GitHubRelease, limit.Token).ConfigureAwait(false);
            return release is { TagName: { Length: > 0 } tag, HtmlUrl: { } url } && IsReleasePage(url, out var page)
                ? new ReleaseLookup(new LatestRelease(tag, page))
                : new ReleaseLookup(null, UpdateCheckFailure.BadAnswer, Detail: "the answer names no release page of Dictum");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReleaseLookup(null, UpdateCheckFailure.TimedOut,
                Detail: string.Create(CultureInfo.InvariantCulture, $"no answer within {(timeout ?? Timeout).TotalSeconds:0.###} s"));
        }
        catch (HttpRequestException ex)
        {
            return new ReleaseLookup(null, UpdateCheckFailure.Unreachable, Detail: ex.Message);
        }
        catch (JsonException ex)
        {
            return new ReleaseLookup(null, UpdateCheckFailure.BadAnswer, Detail: $"the answer is not a release: {ex.Message}");
        }
    }

    private static bool IsRateLimit(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues(RateLimitRemaining, out var remaining) && remaining.FirstOrDefault() == "0");

    private static bool IsReleasePage(string url, out Uri page) =>
        Uri.TryCreate(url, UriKind.Absolute, out page!) && page.Scheme == Uri.UriSchemeHttps
        && page.AbsoluteUri.StartsWith(PagePrefix, StringComparison.Ordinal);
}

/// <summary>The two fields of GitHub's release object that Dictum reads; the rest of the answer is skipped.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }
}
