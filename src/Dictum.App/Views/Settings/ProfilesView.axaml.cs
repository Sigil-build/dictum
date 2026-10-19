using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

/// <summary>
/// The chain reorder. Dragging a row by its handle, or Alt+↑ / Alt+↓ on a focused row, moves the step
/// through <see cref="ProfilesPage.MoveStep"/>, and focus follows the moved row. Rows are 36 px plus a 4 px gap.
/// </summary>
public partial class ProfilesView : UserControl
{
    private const double RowPitch = 40;
    private int? _dragFrom;
    private double _dragStart;
    private Border? _dragged;
    private ProfilesPage? _page;

    public ProfilesView()
    {
        InitializeComponent();
        ChainList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        ChainList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        ChainList.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        ChainList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // The view owns the words box and the pickers: the page takes the text and gives back the part still being
        // typed; a picker clears itself once its pick has been taken.
        WordBox.TextChanged += (_, _) => Typed(WordBox, text => Page?.AddWords(text));
        Pickers.ClearAfterPick(this);
        DataContextChanged += (_, _) =>
        {
            if (_page is not null)
            {
                _page.PropertyChanged -= OnPageChanged;
            }

            _page = Page;
            if (_page is not null)
            {
                _page.PropertyChanged += OnPageChanged;
            }
        };
    }

    /// <summary>Puts the part still being typed back into <paramref name="box"/> after complete entries were taken.</summary>
    internal static void Typed(TextBox box, Func<string, string?> take)
    {
        if (box.Text is not { Length: > 0 } text || take(text) is not { } rest || rest == text)
        {
            return;
        }

        box.Text = rest;
        box.CaretIndex = rest.Length;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A half-typed word belongs to the profile it was typed for.
        if (e.PropertyName == nameof(ProfilesPage.Selected))
        {
            WordBox.Text = "";
        }
    }

    private ProfilesPage? Page => DataContext as ProfilesPage;

    private static Border? Row(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("chainRow"));

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var handle = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("handle"));
        if (handle is null || Row(handle) is not { DataContext: ChainStep step } row)
        {
            return;
        }

        _dragFrom = step.Position - 1;
        _dragStart = e.GetPosition(ChainList).Y;
        _dragged = row;
        row.Classes.Add("dragged");
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is not null)
        {
            _dragged.RenderTransform = new TranslateTransform(0, e.GetPosition(ChainList).Y - _dragStart);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFrom is not { } from || _dragged is null)
        {
            return;
        }

        var count = Page?.Chain.Count ?? 0;
        var to = Math.Clamp(from + (int)Math.Round((e.GetPosition(ChainList).Y - _dragStart) / RowPitch), 0, Math.Max(count - 1, 0));
        _dragged.Classes.Remove("dragged");
        _dragged.RenderTransform = null;
        _dragFrom = null;
        _dragged = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        Move(from, to);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down) || Row(e.Source) is not { DataContext: ChainStep step })
        {
            return;
        }

        var from = step.Position - 1;
        Move(from, e.Key == Key.Up ? from - 1 : from + 1);
        e.Handled = true;
    }

    private void Move(int from, int to)
    {
        if (Page is not { } page || to < 0 || to >= page.Chain.Count || to == from)
        {
            return;
        }

        page.MoveStep(from, to);

        // The rows are rebuilt from the new chain; focus the moved one once they exist.
        Dispatcher.UIThread.Post(() => Row(ChainList.ContainerFromIndex(to)?.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("chainRow")))?.Focus(), DispatcherPriority.Loaded);
    }
}
