namespace Dictum.App.Tray;

/// <summary>The five tray icons: the shape inside the ring is the state.</summary>
public enum TrayGlyph
{
    Idle,
    Recording,
    Processing,
    Error,
    Paused,
}

public enum TrayCommand
{
    None,
    UseProfile,
    PauseHotkeys,
    ResumeHotkeys,
    History,
    Settings,
    Documentation,
    About,

    /// <summary>Opens the page of the newer release that the update check found.</summary>
    OpenUpdate,
    RestartDaemon,
    Quit,
}

public enum TrayMenuItemKind
{
    Command,
    Header,
    Separator,
}

/// <param name="Profile">For <see cref="TrayCommand.UseProfile"/>: the profile id.</param>
/// <param name="Checked">Marks the selected profile.</param>
/// <param name="Detail">A profile's languages, shown after its name.</param>
public sealed record TrayMenuItem(
    TrayMenuItemKind Kind,
    string Label,
    TrayCommand Command = TrayCommand.None,
    string? Profile = null,
    bool Checked = false,
    string? Detail = null)
{
    public static TrayMenuItem Separator { get; } = new(TrayMenuItemKind.Separator, "");

    public static TrayMenuItem Header(string label) => new(TrayMenuItemKind.Header, label);

    public static TrayMenuItem Action(string label, TrayCommand command) => new(TrayMenuItemKind.Command, label, command);

    public static TrayMenuItem ForProfile(string id, bool selected, string? languages) =>
        new(TrayMenuItemKind.Command, id, TrayCommand.UseProfile, id, selected, languages);
}

/// <param name="Attention">Recording or an error; a StatusNotifierItem reports NeedsAttention.</param>
/// <param name="Activate">What a plain click does on panels where it does not open the menu (KDE).</param>
public sealed record TrayView(TrayGlyph Glyph, string Tooltip, bool Attention, IReadOnlyList<TrayMenuItem> Menu, TrayCommand Activate);
