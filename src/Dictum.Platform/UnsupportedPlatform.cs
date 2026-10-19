using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;

namespace Dictum.Platform;

/// <summary>Placeholder services for a platform without hotkey and paste support, such as macOS.</summary>
public static class UnsupportedPlatform
{
    public static PlatformServices Create(string name, string reason) => new(
        name,
        CreateHotkeys: null,
        new NoInjector(reason),
        new UnavailableSecretStore(),
        Autostart: null,
        NullNotifier.Instance,
        _ => Task.FromResult<IReadOnlyList<PlatformCheck>>([new("platform", HealthStatus.Warning(reason))]),
        reason);

    private sealed class NoInjector(string reason) : ITextInjector
    {
        public InjectionCapabilities Capabilities { get; } = new(Paste: false, TypeText: false, RestoreClipboard: false, Backend: "none");

        public Task InjectAsync(string text, InjectionOptions options, CancellationToken ct) =>
            throw new InjectionUnavailableException(reason);
    }
}
