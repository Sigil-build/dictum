using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

/// <summary>
/// The chain reorder. Dragging a row by its handle, or Alt+↑ / Alt+↓ on a focused row, moves the step
/// through <see cref="ProfileChain.Move"/>, and focus follows the moved row.
/// </summary>
public sealed partial class ProfilesView : UserControl
{
    /// <summary>A chain row is 36 px high, plus a 4 px gap.</summary>
    private const double RowPitch = 40;

    private const string ChainRowClass = "chainRow";
    private const string HandleClass = "handle";
    private const string DraggedClass = "dragged";

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
        WordBox.TextChanged += (_, _) => TextBoxes.Typed(WordBox, text => Page?.Words.Add(text));
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
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(ChainRowClass));

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var handle = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(HandleClass));
        if (handle is null || Row(handle) is not { DataContext: ChainStep step } row)
        {
            return;
        }

        _dragFrom = step.Position - 1;
        _dragStart = e.GetPosition(ChainList).Y;
        _dragged = row;
        row.Classes.Add(DraggedClass);
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

        var count = Page?.Chain.Steps.Count ?? 0;
        var to = Math.Clamp(from + (int)Math.Round((e.GetPosition(ChainList).Y - _dragStart) / RowPitch), 0, Math.Max(count - 1, 0));
        _dragged.Classes.Remove(DraggedClass);
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
        if (Page is not { } page || to < 0 || to >= page.Chain.Steps.Count || to == from)
        {
            return;
        }

        page.Chain.Move(from, to);

        // The rows are rebuilt from the new chain; focus the moved one once they exist.
        Dispatcher.UIThread.Post(() => Row(ChainList.ContainerFromIndex(to)?.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains(ChainRowClass)))?.Focus(), DispatcherPriority.Loaded);
    }
}
