using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux.Tray;

namespace Dictum.App.Tray;

/// <summary>
/// The tray on Linux: our StatusNotifierItem, showing the SVG set from <see cref="TraySvgIcons"/>, loose in one
/// folder that the item names as its icon theme path. Thread-safe: <see cref="StatusNotifierItem.Update"/> swaps an
/// immutable state, and the processing animation runs on a timer.
/// </summary>
public sealed class SniTrayPresenter : ITrayPresenter
{
    private static readonly TimeSpan FrameStep = TimeSpan.FromMilliseconds(200);

    private readonly StatusNotifierHost _host;
    private readonly string _iconDirectory;
    private readonly Func<bool> _highContrast;
    private readonly bool _plasma = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Timer _animation;
    private TrayView? _view;
    private IReadOnlyList<TrayMenuItem>? _menu;
    private IReadOnlyDictionary<int, TrayMenuItem> _commands = new Dictionary<int, TrayMenuItem>();
    private int _generation;
    private int _frame;

    private SniTrayPresenter(StatusNotifierHost host, string iconDirectory, Func<bool> highContrast)
    {
        _host = host;
        _iconDirectory = iconDirectory;
        _highContrast = highContrast;
        _animation = new Timer(_ => NextFrame(), null, Timeout.Infinite, Timeout.Infinite);
        host.Item.Activated += (_, _) => Activated?.Invoke();
        host.Menu.Clicked += id =>
        {
            if (_commands.TryGetValue(id, out var item))
            {
                ItemClicked?.Invoke(item);
            }
        };
        host.AvailabilityChanged += _ => AvailabilityChanged?.Invoke();
    }

    public event Action<TrayMenuItem>? ItemClicked;

    public event Action? Activated;

    public event Action? AvailabilityChanged;

    /// <summary>False once the bus says no StatusNotifierWatcher runs (GNOME without the AppIndicator extension).</summary>
    public bool IsAvailable => _host.Availability is not (TrayAvailability.NoWatcher or TrayAvailability.Failed);

    public static async Task<SniTrayPresenter?> TryStartAsync(string iconDirectory, Func<bool> highContrast, ILog log)
    {
        TraySvgIcons.WriteAll(iconDirectory);
        var host = await StatusNotifierHost.TryStartAsync(log).ConfigureAwait(false);
        return host is null ? null : new SniTrayPresenter(host, iconDirectory, highContrast);
    }

    public void Show(TrayView view)
    {
        lock (_gate)
        {
            _view = view;
            if (!ReferenceEquals(_menu, view.Menu) && !SameMenu(_menu, view.Menu))
            {
                _menu = view.Menu;
                var (root, commands) = SniMenu.Build(view.Menu, ++_generation, _plasma);
                _commands = commands;
                _host.Menu.SetMenu(root);
            }

            _animation.Change(view.Glyph == TrayGlyph.Processing ? FrameStep : Timeout.InfiniteTimeSpan, FrameStep);
            Render();
        }
    }

    /// <summary>Redraws with the current contrast setting.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            Render();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _animation.DisposeAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    private void NextFrame()
    {
        lock (_gate)
        {
            _frame = (_frame + 1) % TrayGeometry.ProcessingFrames;
            Render();
        }
    }

    private void Render()
    {
        if (_view is not { } view)
        {
            return;
        }

        var icon = TraySvgIcons.Name(view.Glyph, _frame, colour: !_highContrast());
        _host.Item.Update(new SniState(
            view.Attention ? "NeedsAttention" : "Active", icon, icon, _iconDirectory, "Dictum", "Dictum", view.Tooltip));
    }

    private static bool SameMenu(IReadOnlyList<TrayMenuItem>? a, IReadOnlyList<TrayMenuItem> b) => a is not null && a.SequenceEqual(b);
}
