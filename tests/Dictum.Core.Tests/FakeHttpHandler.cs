using System.Diagnostics;
using System.Net;
using System.Text;

namespace Dictum.Core.Tests;

/// <summary>Records requests (with their bodies read eagerly) and replays canned responses.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string json)
    {
        return Respond(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    public FakeHttpHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _responses.Enqueue((request, _) => Task.FromResult(respond(request)));
        return this;
    }

    public FakeHttpHandler Throw(Exception exception)
    {
        _responses.Enqueue((_, _) => throw exception);
        return this;
    }

    /// <summary>Accepts the request and never answers, until the request is cancelled.</summary>
    public FakeHttpHandler Hang()
    {
        _responses.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });
        return this;
    }

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return _responses.Count > 0
            ? await _responses.Dequeue()(request, cancellationToken)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("no canned response") };
    }
}
