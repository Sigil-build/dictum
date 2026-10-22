using System.Text;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux.DBus;
using Dictum.Processing;
using Dictum.Stt.OpenAi;
using Dictum.Testing;

namespace Dictum.Core.Tests.Linux;

/// <summary>
/// The Secret Service store with no session bus to reach it through: its bus is a socket in a temp folder that nothing
/// listens on, never the session's own bus or keyring.
/// </summary>
public sealed class SecretServiceStoreTests : IDisposable
{
    private const string NoSecretStoreHint =
        "The API key cannot be read: with no OS secret store, give it as 'env:VAR' in the config and set that variable where Dictum starts.";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly TempDirectory _folder = new();
    private readonly Portal _portal;
    private readonly SecretServiceStore _store;

    public SecretServiceStoreTests()
    {
        _portal = new Portal(NullLog.Instance, "unix:path=" + Escape(_folder.File("bus")));
        _store = new SecretServiceStore(_portal, NullLog.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _portal.Dispose();
        _folder.Dispose();
    }

    // Without a bus there is no store at all, as with no Secret Service on the bus: every call says to use env:VAR.
    [Fact]
    public async Task WithNoSessionBusEveryCallSaysToGiveKeysAsEnvVariables()
    {
        var read = await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => _store.GetAsync("dictum-processor-cleanup", _ct));
        var store = await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => _store.SetAsync("dictum-processor-cleanup", "sk-test", _ct));
        var delete = await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => _store.DeleteAsync("dictum-processor-cleanup", _ct));

        Assert.All([read, store, delete], ex => Assert.Contains("use 'env:VAR' for API keys", ex.Message, StringComparison.Ordinal));
    }

    // Neither 'dictum secret set' nor Settings can store a key without the bus, so an LLM processor's and an
    // OpenAI-compatible speech provider's checks name env:VAR for a secret: key, not 'dictum secret set'.
    [Fact]
    public async Task WithNoSessionBusAProvidersCheckSaysToGiveTheKeyAsAnEnvVariable()
    {
        var http = new FakeHttpHandler();
        var values = new ConfigValueResolver(_store);
        var llm = new LlmProcessor(
            "cleanup",
            new LlmProcessorConfig { BaseUrl = "http://llm/v1", Model = "qwen3-4b", Prompt = "cleanup", ApiKey = "secret:dictum-processor-cleanup" },
            http.Client(),
            values,
            new PromptTemplates(_folder.Path),
            NullLog.Instance);
        var stt = new OpenAiCompatibleStt(
            "llama-swap",
            new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:8080/v1/", Model = "whisper-large-v3-turbo", ApiKey = "secret:dictum-stt-llama-swap" },
            http.Client(),
            values,
            NullLog.Instance);

        var checks = new[] { await llm.CheckAsync(_ct), await stt.CheckAsync(_ct) };

        Assert.All(checks, check => Assert.Equal((HealthLevel.Error, NoSecretStoreHint), (check.Level, check.Hint)));
        Assert.Empty(http.Requests);
    }

    /// <summary>A D-Bus address value: every byte but the ones the spec leaves as they are, %-escaped.</summary>
    private static string Escape(string value)
    {
        var escaped = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            escaped.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/' or '.' or '\\' or '*' ? c.ToString() : $"%{b:x2}");
        }

        return escaped.ToString();
    }
}
