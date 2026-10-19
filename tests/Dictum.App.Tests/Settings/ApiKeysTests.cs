using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Settings;

public sealed class ApiKeysTests
{
    private readonly FakeSecrets _secrets = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void ProviderSecretsAreNamedAfterTheProvider() => Assert.Equal("dictum-stt-remote", SecretNames.Stt("remote"));

    // Review focus 2.
    [Fact]
    public async Task ATypedKeyGoesToTheStoreAndTheConfigGetsAReference()
    {
        var result = await ApiKeys.StoreAsync(_secrets, "dictum-stt-remote", "  sk-123  ", _ct);

        Assert.Equal(new KeyResult("secret:dictum-stt-remote", null), result);
        Assert.Equal("sk-123", _secrets.Values["dictum-stt-remote"]);
    }

    [Theory]
    [InlineData("env:OPENAI_API_KEY")]
    [InlineData("secret:shared-key")]
    public async Task AReferenceIsKeptAsTyped(string typed)
    {
        Assert.Equal(new KeyResult(typed, null), await ApiKeys.StoreAsync(_secrets, "dictum-stt-remote", typed, _ct));
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public async Task AnEmptyBoxMeansNoKey() =>
        Assert.Equal(new KeyResult(null, null), await ApiKeys.StoreAsync(_secrets, "dictum-stt-remote", "   ", _ct));

    // Review focus 2.
    [Fact]
    public async Task AStoreThatFailsSavesNothing()
    {
        _secrets.Fails = new ConfigException("No Secret Service is running (KWallet or GNOME Keyring); use 'env:VAR' for API keys instead.");

        var result = await ApiKeys.StoreAsync(_secrets, "dictum-stt-remote", "sk-123", _ct);

        Assert.Null(result.Value);
        Assert.Equal("No Secret Service is running (KWallet or GNOME Keyring); use 'env:VAR' for API keys instead.", result.Error);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public async Task AStoreThatThrowsAtOnceIsAFailureToo()
    {
        var result = await ApiKeys.StoreAsync(new UnavailableSecretStore(), "dictum-stt-remote", "sk-123", _ct);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RemovingDeletesOnlyOurOwnSecret()
    {
        _secrets.Values["dictum-stt-remote"] = "sk-123";
        _secrets.Values["shared-key"] = "sk-456";

        await ApiKeys.RemoveAsync(_secrets, "dictum-stt-remote", "secret:shared-key", new DictumConfig(), NullLog.Instance, _ct);
        Assert.Equal(2, _secrets.Values.Count);

        await ApiKeys.RemoveAsync(_secrets, "dictum-stt-remote", "secret:dictum-stt-remote", new DictumConfig(), NullLog.Instance, _ct);
        Assert.False(_secrets.Values.ContainsKey("dictum-stt-remote"));
    }

    // Final review I3: another provider or processor still holds the reference.
    [Theory]
    [InlineData("stt")]
    [InlineData("processor")]
    public async Task AKeyStillReferencedElsewhereStays(string holder)
    {
        _secrets.Values["dictum-processor-cleanup"] = "sk-123";
        const string reference = "secret:dictum-processor-cleanup";
        var config = new DictumConfig();
        if (holder == "stt")
        {
            config.Stt["remote"] = new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:8080/v1", Model = "whisper-1", ApiKey = reference };
        }
        else
        {
            config.Processors["llm"] = new LlmProcessorConfig { BaseUrl = "http://localhost:8080/v1", Model = "local", Prompt = "llm", ApiKey = reference };
        }

        await ApiKeys.RemoveAsync(_secrets, "dictum-processor-cleanup", reference, config, NullLog.Instance, _ct);

        Assert.Equal("sk-123", _secrets.Values["dictum-processor-cleanup"]);
    }
}
