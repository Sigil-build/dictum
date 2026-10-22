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
    /// <summary>
    /// Avalonia 12's Win32 platform adds its tray icons on its message window, whose class is this and a GUID, and
    /// sends their events as WM_USER + 1024 (<c>TrayIconImpl.CustomWindowsMessage.WM_TRAYMOUSE</c>), with the icon's
    /// id in wParam. TrayBalloonTests checks both against the real platform.
    /// </summary>
    public const string MessageWindowClass = "AvaloniaMessageWindow ";

    public const uint TrayCallbackMessage = 0x0400 + 1024;

    private readonly TrayIcon _icon = new() { ToolTipText = AppNames.Product };
    private readonly DispatcherTimer _animation = new() { Interval = TrayGeometry.FrameStep };
    private readonly Application _app;
    private readonly Func<bool> _highContrast;
    private TrayView? _view;
    private IReadOnlyList<TrayMenuItem>? _menu;
    private NotifyIconBalloon? _balloon;
    private Action? _balloonClicked;
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

    /// <summary>
    /// A notification on this icon: a balloon, which Windows 10 and 11 show as a toast. <paramref name="clicked"/> runs on
    /// the UI thread when the user clicks it. False when the icon cannot be reached. UI thread.
    /// </summary>
    public bool Notify(string title, string text, Action clicked)
    {
        if (_balloon is null)
        {
            _balloon = NotifyIconBalloon.Find(MessageWindowClass, TrayCallbackMessage);
            if (_balloon is null)
            {
                return false;
            }

            _balloon.Clicked += () => Dispatcher.UIThread.Post(() => _balloonClicked?.Invoke());
        }

        _balloonClicked = clicked;
        return _balloon.Show(title, text);
    }

    public ValueTask DisposeAsync()
    {
        _animation.Stop();
        _balloon?.Dispose();
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

        // WindowIcon encodes the bitmap at once, so it can be disposed here.
        using var bitmap = TrayIconRenderer.Render(_view.Glyph, _frame, _size, _mono, colourLayer: !_highContrast());
        _icon.Icon = new WindowIcon(bitmap);
    }
}
