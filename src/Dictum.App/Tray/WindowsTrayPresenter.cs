using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.Platform.Windows;

namespace Dictum.App.Tray;

/// <summary>
/// The tray on Windows: Avalonia's <see cref="TrayIcon"/>, with icons drawn by <see cref="TrayIconRenderer"/> at the
/// taskbar's icon size and in its colour. Create it on the UI thread after the framework is initialised.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayPresenter : ITrayPresenter
{
    private readonly TrayIcon _icon = new() { ToolTipText = "Dictum" };
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Application _app;
    private readonly Func<bool> _highContrast;
    private TrayView? _view;
    private IReadOnlyList<TrayMenuItem>? _menu;
    private Color _mono;
    private int _size;
    private int _frame;

    public WindowsTrayPresenter(Application app, Func<bool> highContrast)
    {
        _app = app;
        _highContrast = highContrast;
        ReadTaskbar();
        _icon.Clicked += (_, _) => Activated?.Invoke();
        TrayIcon.SetIcons(app, [_icon]);
        _animation.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % TrayGeometry.ProcessingFrames;
            Render();
        };
    }

    public event Action<TrayMenuItem>? ItemClicked;

    public event Action? Activated;

    // The notification area is always there on Windows: nothing ever changes availability.
    event Action? ITrayPresenter.AvailabilityChanged
    {
        add { }
        remove { }
    }

    public bool IsAvailable => true;

    public void Show(TrayView view)
    {
        _view = view;
        if (_menu is null || !_menu.SequenceEqual(view.Menu))
        {
            _menu = view.Menu;
            _icon.Menu = TrayNativeMenu.Build(view.Menu, item => ItemClicked?.Invoke(item));
        }

        _icon.ToolTipText = view.Tooltip;
        if (view.Glyph == TrayGlyph.Processing)
        {
            _animation.Start();
        }
        else
        {
            _animation.Stop();
            _frame = 0;
        }

        Render();
    }

    public void Refresh()
    {
        ReadTaskbar();
        Render();
    }

    public ValueTask DisposeAsync()
    {
        _animation.Stop();
        _icon.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ReadTaskbar()
    {
        var light = TrayPanel.IsLight(_app.PlatformSettings?.GetColorValues(), TaskbarInfo.UsesLightTheme);
        _mono = Color.Parse(light ? TrayGeometry.MonoOnLight : TrayGeometry.MonoOnDark);
        _size = TaskbarInfo.SmallIconSize();
    }

    private void Render()
    {
        if (_view is null)
        {
            return;
        }

        // WindowIcon encodes the bitmap at once, so it can be disposed here (research §7).
        using var bitmap = TrayIconRenderer.Render(_view.Glyph, _frame, _size, _mono, colourLayer: !_highContrast());
        _icon.Icon = new WindowIcon(bitmap);
    }
}
