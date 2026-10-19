using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.Core.Tests.Configuration;

/// <summary>A record prints its members wherever it is formatted, so the records that can hold an API key print it masked.</summary>
public sealed class KeyRedactionTests
{
    private const string Key = "sk-live-4f9a2c";
    private const string Server = "http://localhost:8080/v1";

    [Fact]
    public void APendingSecretPrintsItsNameButNotTheKey() =>
        Assert.Equal("PendingSecret { Name = dictum-processor-cleanup, Value = *** }", new PendingSecret("dictum-processor-cleanup", Key).ToString());

    [Fact]
    public void AServerPrintsItsAddressButNotTheKey() =>
        Assert.Equal($"ExistingLlmServer {{ BaseUrl = {Server}, Model = qwen3-4b, ApiKey = *** }}", new ExistingLlmServer(Server, "qwen3-4b", Key).ToString());

    [Fact]
    public void ProviderSettingsWithALiteralKeyPrintItMasked()
    {
        object[] settings =
        [
            new LlmProcessorConfig { BaseUrl = Server, Model = "qwen3-4b", Prompt = LlmProcessorConfig.CleanupPrompt, ApiKey = Key },
            new OpenAiCompatibleSttConfig { BaseUrl = Server, Model = "whisper-1", ApiKey = Key },
        ];

        Assert.All(settings, s =>
        {
            var text = s.ToString()!;
            Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
            Assert.Contains($"BaseUrl = {Server}", text, StringComparison.Ordinal);
            Assert.Contains("ApiKey = ***", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AKeyReferenceIsPrintedAsItIs()
    {
        var stt = new OpenAiCompatibleSttConfig { BaseUrl = Server, Model = "whisper-1", ApiKey = "env:OPENAI_API_KEY" };

        Assert.Contains("ApiKey = env:OPENAI_API_KEY", stt.ToString(), StringComparison.Ordinal);
        Assert.Contains("ApiKey = secret:dictum-processor-cleanup", new ExistingLlmServer(Server, "qwen3-4b", "secret:dictum-processor-cleanup").ToString(), StringComparison.Ordinal);
    }
}
