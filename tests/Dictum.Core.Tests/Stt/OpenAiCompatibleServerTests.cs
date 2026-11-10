using Dictum.Core.Configuration;
using Dictum.Core.Http;
using Dictum.Testing;

namespace Dictum.Core.Tests.Stt;

public sealed class OpenAiCompatibleServerTests
{
    /// <summary>
    /// HttpClient copies the bytes of a malformed answer into its exception's message. A plugin provider's server can
    /// send such bytes, so the message the user sees spells a control character out.
    /// </summary>
    [Fact]
    public async Task AnUnreachableMessageHoldingTheServersBytesIsSpeltOut()
    {
        var handler = new FakeHttpHandler().Throw(new HttpRequestException("Received an invalid header name: 'X\u001b]0;PWNED\u0007'"));
        var server = new OpenAiCompatibleServer(handler.Client(), new ConfigValueResolver(new FakeSecrets()), "STT 'p'", "http://127.0.0.1:1/v1", null);

        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => server.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:1/v1/x"), 1000, TestContext.Current.CancellationToken));

        Assert.Equal("STT 'p' is unreachable at http://127.0.0.1:1/v1: Received an invalid header name: 'X\\u001b]0;PWNED\\u0007'", error.Message);
    }
}
