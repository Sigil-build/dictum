using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Platform.Injection;

/// <summary>
/// Clipboard + simulated paste: save the clipboard, put the text on it, wait for the hotkey modifiers
/// to be released, send Ctrl+V (Ctrl+Shift+V for terminals), then restore the previous text unless
/// something else changed the clipboard in the meantime.
/// </summary>
public sealed class ClipboardPasteInjector(
    IClipboard clipboard,
    IKeystrokeSender? keystrokes,
    IActiveAppProvider? activeApp,
    IModifierState? modifiers,
    Func<IReadOnlyCollection<string>> terminalApps,
    ILog log) : ITextInjector
{
    private static readonly TimeSpan ModifierTimeout = TimeSpan.FromSeconds(2);

    public InjectionCapabilities Capabilities { get; } = new(
        Paste: keystrokes is not null,
        TypeText: false,
        RestoreClipboard: true,
        Backend: keystrokes is null ? clipboard.Backend : $"{clipboard.Backend} + {keystrokes.Backend}");

    public async Task InjectAsync(string text, InjectionOptions options, CancellationToken ct)
    {
        switch (options.Mode)
        {
            case OutputMode.Clipboard:
                await clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
                return;
            case OutputMode.Type:
                throw new InjectionUnavailableException(
                    "Typing text character by character is not supported yet.",
                    "Use \"output\": \"paste\" or \"clipboard\" in the profile.");
        }

        if (keystrokes is null)
        {
            throw new InjectionUnavailableException("No way to send the paste keystroke on this system; the text is on the clipboard.");
        }

        var previous = await TryGetAsync(ct).ConfigureAwait(false);
        await clipboard.SetTextAsync(text, ct).ConfigureAwait(false);

        if (modifiers is not null)
        {
            await modifiers.WaitForReleaseAsync(ModifierTimeout, ct).ConfigureAwait(false);
        }

        var keys = ChooseKeys(options.Shortcut);
        await keystrokes.SendPasteAsync(keys, ct).ConfigureAwait(false);
        log.Debug($"Pasted {log.Redact(text)} with {keys} via {keystrokes.Backend}");

        if (previous is null)
        {
            return;
        }

        await Task.Delay(options.RestoreDelay, ct).ConfigureAwait(false);
        if (await TryGetAsync(ct).ConfigureAwait(false) == text)
        {
            await clipboard.SetTextAsync(previous, ct).ConfigureAwait(false);
        }
        else
        {
            log.Debug("Clipboard changed after paste; not restoring the previous content");
        }
    }

    private PasteKeys ChooseKeys(PasteShortcut shortcut) => shortcut switch
    {
        PasteShortcut.CtrlV => PasteKeys.CtrlV,
        PasteShortcut.CtrlShiftV => PasteKeys.CtrlShiftV,
        _ => activeApp?.GetActiveApp() is { } app && terminalApps().Contains(app, StringComparer.OrdinalIgnoreCase)
            ? PasteKeys.CtrlShiftV
            : PasteKeys.CtrlV,
    };

    private async Task<string?> TryGetAsync(CancellationToken ct)
    {
        try
        {
            return await clipboard.GetTextAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Debug($"Could not read the clipboard: {ex.Message}");
            return null;
        }
    }
}
