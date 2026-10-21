using System.ComponentModel;
using Dictum.Core.Configuration;
using Dictum.Testing;

namespace Dictum.Core.Tests.Configuration;

public class ConfigValueResolverTests
{
    private readonly ConfigValueResolver _resolver = new(new FakeSecrets { Values = { ["groq"] = "s3cret" } });

    [Fact]
    public async Task LiteralAndEmptyValuesPassThrough()
    {
        Assert.Equal("plain", await _resolver.ResolveAsync("plain", TestContext.Current.CancellationToken));
        Assert.Null(await _resolver.ResolveAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnvReferenceReadsTheVariable()
    {
        var name = $"DICTUM_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            Assert.Equal("from-env", await _resolver.ResolveAsync($"env:{name}", TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task MissingEnvVariableIsAConfigError() =>
        await Assert.ThrowsAsync<ConfigException>(async () =>
            await _resolver.ResolveAsync("env:DICTUM_TEST_SURELY_MISSING", TestContext.Current.CancellationToken));

    [Fact]
    public async Task SecretReferenceReadsTheStore()
    {
        Assert.Equal("s3cret", await _resolver.ResolveAsync("secret:groq", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ConfigException>(async () =>
            await _resolver.ResolveAsync("secret:other", TestContext.Current.CancellationToken));
    }

    // The type tells hints that no store can keep a key here, so they name env:VAR instead of storing it.
    [Fact]
    public async Task UnavailableStoreExplainsTheEnvFallback()
    {
        var resolver = new ConfigValueResolver(new UnavailableSecretStore());

        var ex = await Assert.ThrowsAsync<SecretStoreUnavailableException>(async () =>
            await resolver.ResolveAsync("secret:groq", TestContext.Current.CancellationToken));

        Assert.Contains("env:", ex.Message);
    }

    // Credential Manager throws Win32Exception for any error but not-found, and the keyring's D-Bus can fail to connect.
    // Either way the key cannot be read, which a processor's on-error choice and doctor's key hint handle.
    [Fact]
    public async Task AStoreThatFailsToReadIsAKeyThatCannotBeRead()
    {
        var failure = new Win32Exception(1312, "Reading from Credential Manager failed");
        var resolver = new ConfigValueResolver(new FakeSecrets { ReadFails = failure });

        var ex = await Assert.ThrowsAsync<ConfigException>(async () =>
            await resolver.ResolveAsync("secret:groq", TestContext.Current.CancellationToken));

        Assert.Equal("Secret 'groq' cannot be read from the OS secret store: Reading from Credential Manager failed", ex.Message);
        Assert.Same(failure, ex.InnerException);
    }

    [Fact]
    public async Task ACancelledReadStaysACancellation()
    {
        var resolver = new ConfigValueResolver(new FakeSecrets { ReadFails = new OperationCanceledException() });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync("secret:groq", TestContext.Current.CancellationToken));
    }
}
