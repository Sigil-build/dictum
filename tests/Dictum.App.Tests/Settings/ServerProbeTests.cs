using System.Net;
using System.Text;
using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

public sealed class ServerProbeTests
{
    private const string Url = "http://localhost:11434/v1";
    private readonly Stub _server = new();
    private readonly FakeSecrets _secrets = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private ServerProbe Probe(TimeSpan? timeout = null) => new(new HttpClient(_server), _secrets, TimeProvider.System, timeout);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string TwoModels = """{ "object": "list", "data": [ { "id": "qwen2.5:7b" }, { "id": "llama3.1" } ] }""";

    [Fact]
    public async Task AListedModelIsReachableAndTimed()
    {
        _server.Answer = _ => Json(TwoModels);

        var check = await Probe().CheckAsync(Url + "/", "qwen2.5:7b", "sk-1", _ct);

        Assert.True(check.Ok);
        Assert.StartsWith("Reachable · ", check.Message);
        Assert.EndsWith(" ms", check.Message);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("http://localhost:11434/v1/models", request.Uri);
        Assert.Equal("Bearer sk-1", request.Authorization);
    }

    [Fact]
    public async Task AModelTheServerDoesNotHaveIsNamed()
    {
        _server.Answer = _ => Json(TwoModels);

        var check = await Probe().CheckAsync(Url, "gemma3", null, _ct);

        Assert.Equal(new ServerCheck(false, "Reachable, but the server has no model “gemma3”. It has: qwen2.5:7b, llama3.1."), check);
        Assert.Null(_server.Requests.Single().Authorization);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{ "data": [] }""")]
    public async Task AServerThatListsNothingIsAccepted(string body)
    {
        _server.Answer = _ => Json(body);

        Assert.True((await Probe().CheckAsync(Url, "anything", null, _ct)).Ok);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "The server rejected the API key.")]
    [InlineData(HttpStatusCode.Forbidden, "The server rejected the API key.")]
    [InlineData(HttpStatusCode.InternalServerError, "The server answered 500.")]
    public async Task AnErrorStatusSaysWhat(HttpStatusCode status, string message)
    {
        _server.Answer = _ => Json("{}", status);

        Assert.Equal(new ServerCheck(false, message), await Probe().CheckAsync(Url, "qwen2.5:7b", null, _ct));
    }

    [Fact]
    public async Task AnUnreachableServerSaysWhy()
    {
        _server.Answer = _ => throw new HttpRequestException("Connection refused");

        Assert.Equal(new ServerCheck(false, "Could not reach http://localhost:11434/v1: Connection refused"),
            await Probe().CheckAsync(Url, "qwen2.5:7b", null, _ct));
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerTimesOut()
    {
        _server.Hang = true;

        Assert.Equal(new ServerCheck(false, "No answer from http://localhost:11434/v1."),
            await Probe(TimeSpan.FromMilliseconds(50)).CheckAsync(Url, "qwen2.5:7b", null, _ct));
    }

    [Theory]
    [InlineData("localhost:8080", "qwen", "Enter a URL that starts with http:// or https://, e.g. http://localhost:8080/v1.")]
    [InlineData("http://localhost:8080/v1", " ", "Enter the model's name on that server.")]
    public async Task ABadUrlOrNoModelIsCaughtBeforeAnyRequest(string url, string model, string message)
    {
        Assert.Equal(new ServerCheck(false, message), await Probe().CheckAsync(url, model, null, _ct));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task AStoredKeyIsReadFromTheKeyring()
    {
        _secrets.Values["dictum-processor-cleanup"] = "sk-stored";
        _server.Answer = _ => Json(TwoModels);

        await Probe().CheckAsync(Url, "qwen2.5:7b", "secret:dictum-processor-cleanup", _ct);

        Assert.Equal("Bearer sk-stored", _server.Requests.Single().Authorization);
    }

    [Fact]
    public async Task AMissingStoredKeySaysSo()
    {
        var check = await Probe().CheckAsync(Url, "qwen2.5:7b", "secret:nowhere", _ct);

        Assert.False(check.Ok);
        Assert.StartsWith("The stored API key could not be read: ", check.Message);
        Assert.Empty(_server.Requests);
    }

    private sealed class Stub : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public bool Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Answer(request);
        }
    }
}
