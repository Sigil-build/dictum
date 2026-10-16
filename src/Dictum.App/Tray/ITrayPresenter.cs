namespace Dictum.App.Tray;

/// <summary>Draws a <see cref="TrayView"/> in the OS tray and reports clicks.</summary>
public interface ITrayPresenter : IAsyncDisposable
{
    /// <summary>A menu item was chosen. May be raised on any thread.</summary>
    event Action<TrayMenuItem>? ItemClicked;

    /// <summary>A plain click where it does not open the menu. May be raised on any thread.</summary>
    event Action? Activated;

    /// <summary><see cref="IsAvailable"/> may have changed. May be raised on any thread.</summary>
    event Action? AvailabilityChanged;

    /// <summary>False when this desktop has no tray to show the icon in.</summary>
    bool IsAvailable { get; }

    /// <summary>UI thread.</summary>
    void Show(TrayView view);

    /// <summary>The panel's colour or the contrast setting changed; draws again. UI thread.</summary>
    void Refresh();
}
