using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;

namespace Dictum.Platform.Hotkeys;

public enum HotkeyPhase
{
    Pressed,
    Released,
}

/// <param name="Id">Stable id, e.g. the profile id; portals remember bindings by it.</param>
public sealed record HotkeyBinding(string Id, Hotkey Hotkey, string Description);

public sealed record HotkeyEvent(string BindingId, HotkeyPhase Phase);

/// <param name="SupportsRelease">Released events arrive, so push-to-talk works.</param>
/// <param name="NeedsUserConfirmation">The desktop asks the user before binding (Wayland portals).</param>
/// <param name="CanSuppress">The combination is swallowed instead of reaching the focused app.</param>
public sealed record HotkeyCapabilities(bool SupportsRelease, bool NeedsUserConfirmation, bool CanSuppress, string Backend);

public interface IHotkeyService : IAsyncDisposable
{
    HotkeyCapabilities Capabilities { get; }

    /// <summary>
    /// Replaces all bindings at once. Portals allow binding only once per session, so a change of any
    /// binding re-registers the whole set.
    /// </summary>
    Task SetBindingsAsync(IReadOnlyList<HotkeyBinding> bindings, CancellationToken ct);

    /// <summary>Raised off the hook thread.</summary>
    event Action<HotkeyEvent>? Triggered;
}

/// <summary>The backend could not register hotkeys (no portal, permission denied, hook failed).</summary>
public sealed class HotkeyUnavailableException(string message, string? hint = null, Exception? inner = null)
    : Exception(message, inner), IHasHint
{
    public string? Hint { get; } = hint;
}
