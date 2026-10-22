using System.Net;
using System.Text;
using Dictum.App.Updates;

namespace Dictum.App.Tests.Updates;

/// <summary>The latest-release request against a fake handler; never the real GitHub API.</summary>
public sealed class GitHubReleasesTests : IDisposable
{
    private const string Release = """
        {
          "url": "https://api.github.com/repos/Sigil-build/dictum/releases/1",
          "html_url": "https://github.com/Sigil-build/dictum/releases/tag/v0.5.0",
          "tag_name": "v0.5.0",
          "name": "Dictum 0.5.0",
          "draft": false,
          "prerelease": false,
          "assets": [ { "name": "dictum-0.5.0-win-x64.msi", "size": 41234567 } ],
          "body": "What is new"
        }
        """;

    private readonly FakeGitHub _github = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _github.Dispose();

    private GitHubReleases Releases(TimeSpan? timeout = null) => new(new HttpClient(_github), "0.4.0", timeout);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TheLatestReleaseIsItsTagAndPage()
    {
        _github.Answer = _ => Json(Release);

        var lookup = await Releases().LatestAsync(_ct);

        Assert.Equal(new LatestRelease("v0.5.0", new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.5.0")), lookup.Release);
        Assert.Equal(UpdateCheckFailure.None, lookup.Failure);
    }

    // Unauthenticated, as GitHub's REST API asks: a User-Agent with the version, and its media type.
    [Fact]
    public async Task TheRequestNamesDictumsVersionAndGitHubsMediaType()
    {
        _github.Answer = _ => Json(Release);

        await Releases().LatestAsync(_ct);

        var request = Assert.Single(_github.Requests);
        Assert.Equal("https://api.github.com/repos/Sigil-build/dictum/releases/latest", request.Uri);
        Assert.Equal("Dictum/0.4.0", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Null(request.Authorization);
    }

    // A repository without a release, or one whose releases are all drafts or pre-releases.
    [Fact]
    public async Task NotFoundIsAFailure()
    {
        _github.Answer = _ => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound);

        var lookup = await Releases().LatestAsync(_ct);

        Assert.Equal((null, UpdateCheckFailure.Answered, 404), (lookup.Release, lookup.Failure, lookup.StatusCode));
    }

    // 60 requests an hour per address without a token; one more is 403 with no requests remaining.
    [Fact]
    public async Task TheRateLimitIsAFailureOfItsOwn()
    {
        _github.Answer = _ =>
        {
            var response = Json("""{ "message": "API rate limit exceeded" }""", HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            return response;
        };

        var lookup = await Releases().LatestAsync(_ct);

        Assert.Equal((null, UpdateCheckFailure.RateLimited), (lookup.Release, lookup.Failure));
    }

    [Fact]
    public async Task TooManyRequestsIsTheRateLimitToo()
    {
        _github.Answer = _ => Json("{}", HttpStatusCode.TooManyRequests);

        Assert.Equal(UpdateCheckFailure.RateLimited, (await Releases().LatestAsync(_ct)).Failure);
    }

    [Fact]
    public async Task AnotherForbiddenIsAnAnswer()
    {
        _github.Answer = _ => Json("""{ "message": "Forbidden" }""", HttpStatusCode.Forbidden);

        var lookup = await Releases().LatestAsync(_ct);

        Assert.Equal((UpdateCheckFailure.Answered, 403), (lookup.Failure, lookup.StatusCode));
    }

    [Fact]
    public async Task ASilentServerTimesOut()
    {
        _github.Hang = true;

        var lookup = await Releases(TimeSpan.FromMilliseconds(50)).LatestAsync(_ct);

        Assert.Equal((null, UpdateCheckFailure.TimedOut), (lookup.Release, lookup.Failure));
    }

    [Fact]
    public void TheTimeoutIsTenSeconds() => Assert.Equal(TimeSpan.FromSeconds(10), GitHubReleases.Timeout);

    [Fact]
    public async Task NoConnectionIsAFailure()
    {
        _github.Answer = _ => throw new HttpRequestException("No such host is known. (api.github.com:443)");

        var lookup = await Releases().LatestAsync(_ct);

        Assert.Equal((UpdateCheckFailure.Unreachable, "No such host is known. (api.github.com:443)"), (lookup.Failure, lookup.Detail));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "v0.5.0" }""")]
    [InlineData("""{ "html_url": "https://github.com/Sigil-build/dictum/releases/tag/v0.5.0" }""")]
    [InlineData("""{ "tag_name": 5, "html_url": "https://github.com/Sigil-build/dictum/releases/tag/v0.5.0" }""")]
    public async Task AnAnswerThatIsNoReleaseIsAFailure(string body)
    {
        _github.Answer = _ => Json(body);

        var lookup = await Releases().LatestAsync(_ct);

        Assert.Equal((null, UpdateCheckFailure.BadAnswer), (lookup.Release, lookup.Failure));
    }

    // The page opens in the browser: only Dictum's own releases on github.com, over https.
    [Theory]
    [InlineData("http://github.com/Sigil-build/dictum/releases/tag/v0.5.0")]
    [InlineData("https://example.com/Sigil-build/dictum/releases/tag/v0.5.0")]
    [InlineData("https://github.com/someone/else/releases/tag/v0.5.0")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task APageElsewhereIsAFailure(string page)
    {
        _github.Answer = _ => Json($$"""{ "tag_name": "v0.5.0", "html_url": "{{page}}" }""");

        Assert.Equal(UpdateCheckFailure.BadAnswer, (await Releases().LatestAsync(_ct)).Failure);
    }

    [Fact]
    public async Task TheCallersCancelIsNotATimeout()
    {
        _github.Hang = true;
        using var quit = new CancellationTokenSource();
        var lookup = Releases().LatestAsync(quit.Token);

        await quit.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(string Uri, string? UserAgent, string? Accept, string? Authorization)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public bool Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString(),
                request.Headers.Authorization?.ToString()));
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Answer(request);
        }
    }
}
