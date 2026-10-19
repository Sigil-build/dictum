using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Platform.Injection;

/// <param name="Shortcut">Explicit paste shortcut, or Auto to decide by the active app.</param>
/// <param name="RestoreDelay">Wait after the paste keystroke before restoring the previous clipboard.</param>
public sealed record InjectionOptions(OutputMode Mode, PasteShortcut Shortcut, TimeSpan RestoreDelay);

public sealed record InjectionCapabilities(bool Paste, bool TypeText, bool RestoreClipboard, string Backend);

public interface ITextInjector
{
    InjectionCapabilities Capabilities { get; }

    Task InjectAsync(string text, InjectionOptions options, CancellationToken ct);
}

/// <summary>Plain-text clipboard access; non-text content reads as null and is not restored.</summary>
public interface IClipboard
{
    string Backend { get; }

    Task<string?> GetTextAsync(CancellationToken ct);

    Task SetTextAsync(string text, CancellationToken ct);
}

public enum PasteKeys
{
    CtrlV,
    CtrlShiftV,
}

/// <summary>Sends the paste keystroke to the focused app.</summary>
public interface IKeystrokeSender
{
    string Backend { get; }

    Task SendPasteAsync(PasteKeys keys, CancellationToken ct);
}

/// <summary>Name of the focused app (process or window class), where the platform exposes it.</summary>
public interface IActiveAppProvider
{
    string? GetActiveApp();
}

/// <summary>Lets the injector wait until the hotkey modifiers are released.</summary>
public interface IModifierState
{
    Task WaitForReleaseAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed class InjectionUnavailableException(string message, string? hint = null, Exception? inner = null)
    : Exception(message, inner), IHasHint
{
    public string? Hint { get; } = hint;
}

/// <summary>A backend that can do slow setup (e.g. a permission dialog) before the first use.</summary>
public interface IPreparable
{
    Task PrepareAsync(CancellationToken ct);
}
