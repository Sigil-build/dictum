using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Platform.Injection;

namespace Dictum.Host.Daemon;

/// <summary>Sends the pipeline result to the platform injector with a profile's output settings.</summary>
public sealed class InjectorOutput(ITextInjector injector, InjectionOptions options) : ITextOutput
{
    public static InjectorOutput For(ITextInjector injector, ProfileConfig profile, GeneralSettings general) =>
        For(injector, profile.Output, profile.PasteShortcut, general);

    public static InjectorOutput For(ITextInjector injector, OutputMode mode, PasteShortcut shortcut, GeneralSettings general) =>
        new(injector, new InjectionOptions(mode, shortcut, TimeSpan.FromMilliseconds(general.PasteDelayMs)));

    public Task WriteAsync(string text, CancellationToken ct) => injector.InjectAsync(text, options, ct);
}
