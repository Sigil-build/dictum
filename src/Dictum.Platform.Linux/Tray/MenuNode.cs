namespace Dictum.Platform.Linux.Tray;

/// <summary>One dbusmenu node. Id 0 is the root. Ids must be stable; Plasma never re-reads type,
/// toggle-type or children-display for an id it already knows, so changing those needs a new id.</summary>
public sealed record MenuNode(
    int Id,
    string Label = "",
    bool Enabled = true,
    bool Visible = true,
    bool Separator = false,
    string ToggleType = "",   // "", "checkmark", "radio"
    int ToggleState = -1,     // 0 off, 1 on, -1 indeterminate
    string IconName = "",
    string[][]? Shortcut = null, // [["Control","Alt","space"]] - shown right-aligned by Qt
    IReadOnlyList<MenuNode>? Children = null);
