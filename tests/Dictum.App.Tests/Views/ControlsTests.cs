using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Resources;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public class ControlsTests(AvaloniaFixture fx)
{
    private static readonly string[] Sizes = ["Small", "Medium", "Large"];
    private static readonly string[] RowIcons = ["icon.login", "icon.microphone", "icon.sounds", "icon.history", "icon.language", "icon.overlay"];

    [Fact]
    public Task ASettingsRowShowsItsTextsControlAndError() => fx.Ui(() =>
    {
        var row = new SettingsRow
        {
            Label = "Sounds",
            Description = "A short tick",
            Error = "must be zero or positive",
            Content = new Button { Content = "Preview" },
            Footer = new TextBlock { Text = "second line" },
        };
        var window = new Window { Content = row, Width = 600, Height = 200 };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Sounds", texts);
        Assert.Contains("A short tick", texts);
        Assert.Contains("must be zero or positive", texts);
        Assert.Contains("second line", texts);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Preview"));
        window.Close();
    });

    [Fact]
    public Task ARowWithoutDescriptionErrorOrFooterHidesThem() => fx.Ui(() =>
    {
        var row = new SettingsRow { Label = "Sounds" };
        var window = new Window { Content = row, Width = 600, Height = 200 };
        window.Show();

        var visible = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
        Assert.Equal(["Sounds"], visible.Select(t => t.Text));
        window.Close();
    });

    // A label wraps between its words beside the row's control; when a word of it would not fit there, as in a narrow
    // window or a wide font, the control goes under the label, where the label has the row's width.
    [Theory]
    [InlineData(600, true)]
    [InlineData(460, false)]
    public Task ASettingsRowPutsItsControlUnderTheLabelRatherThanBreakAWord(double width, bool beside) => fx.Ui(() =>
    {
        var control = new Border { Width = 360, Height = 32 };
        var row = new SettingsRow { Label = "Colour theme", Content = control };
        var window = new Window { Content = row, Width = width, Height = 200 };
        window.Show();

        var label = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Colour theme");
        var (labelBox, controlBox) = (Within(label, window), Within(control, window));
        Assert.Single(label.TextLayout.TextLines);
        if (beside)
        {
            Assert.True(controlBox.Left > labelBox.Right, $"{controlBox} is not beside the label's {labelBox}");
            Assert.Equal(labelBox.Center.Y, controlBox.Center.Y, precision: 0);
        }
        else
        {
            Assert.True(controlBox.Top >= labelBox.Bottom, $"{controlBox} is not under the label's {labelBox}");
            Assert.Equal(labelBox.Left, controlBox.Left, precision: 0);
            Assert.True(controlBox.Right <= Within(row, window).Right, $"{controlBox} runs past the row's {Within(row, window)}");
        }

        window.Close();
    });

    // A placeholder wider than its box ends in an ellipsis instead of running under the box's edge, in a text box and in
    // a dropdown; once something is typed, the text box scrolls sideways again.
    [Fact]
    public Task APlaceholderWiderThanItsBoxEndsInAnEllipsis() => fx.Ui(() =>
    {
        const string Placeholder = "A placeholder a good deal wider than its box";
        var box = new TextBox { Width = 140, PlaceholderText = Placeholder };
        var dropdown = new ComboBox { Width = 140, PlaceholderText = Placeholder };
        var window = new Window { Content = new StackPanel { Children = { box, dropdown } }, Width = 400, Height = 200 };
        window.Show();

        Control[] owners = [box, dropdown];
        foreach (var owner in owners)
        {
            var text = owner.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Placeholder && t.IsEffectivelyVisible);
            Assert.True(text.TextLayout.TextLines[0].HasCollapsed, $"The {owner.GetType().Name}'s placeholder is not trimmed");
            Assert.True(Within(text, owner).Right <= owner.Bounds.Width, $"The {owner.GetType().Name}'s placeholder spans {Within(text, owner)}");
        }

        box.Text = Placeholder + Placeholder;
        Dispatcher.UIThread.RunJobs();
        var scroll = box.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width, $"The typed text does not scroll: extent {scroll.Extent}, viewport {scroll.Viewport}");
        window.Close();
    });

    private static Rect Within(Visual visual, Visual ancestor) =>
        new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(ancestor) ?? Matrix.Identity);

    [Theory]
    [InlineData(HealthLevel.Ok, "color.status.ok.bg")]
    [InlineData(HealthLevel.Warning, "color.status.warn.bg")]
    [InlineData(HealthLevel.Error, "color.status.error.bg")]
    public Task TheBadgeIsDrawnInItsLevelsColours(HealthLevel level, string key) => fx.Ui(() =>
    {
        var badge = new StatusBadge { Level = level };
        badge.Measure(Size.Infinity);
        badge.Arrange(new Rect(badge.DesiredSize));
        using var bitmap = new RenderTargetBitmap(new PixelSize(24, 24));
        bitmap.Render(badge);

        Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush));
        var expected = ((ISolidColorBrush)brush!).Color;
        var (b, g, r, a) = Pixels.At(Pixels.Of(bitmap), 24, 4, 12);
        Assert.Equal(255, a);
        Assert.Equal((expected.R, expected.G, expected.B), (r, g, b));
    });

    [Fact]
    public Task TheBadgeIsNamedForScreenReaders() => fx.Ui(() =>
    {
        var badge = new StatusBadge { Level = HealthLevel.Warning };

        Assert.Equal(Strings.StatusWarning, AutomationProperties.GetName(badge));

        badge.Level = HealthLevel.Error;
        Assert.Equal(Strings.StatusError, AutomationProperties.GetName(badge));
    });

    [Fact]
    public Task TheSegmentedListLaysItsItemsOutInARow() => fx.Ui(() =>
    {
        var list = new ListBox { Classes = { "segmented" }, ItemsSource = Sizes, SelectedIndex = 1 };
        var window = new Window { Content = list, Width = 600, Height = 200 };
        window.Show();

        var items = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(3, items.Count);
        Assert.True(items[1].Bounds.X > items[0].Bounds.X);
        Assert.Equal(items[0].Bounds.Y, items[1].Bounds.Y);
        window.Close();
    });

    // A row too narrow for the label and the field side by side wraps the field under its label, at the row's width.
    [Fact]
    public Task AFieldRowPutsTheFieldBesideItsLabelOrUnderIt() => fx.Ui(() =>
    {
        var label = new TextBlock { Text = "Hotkey" };
        var field = new Border { Height = 30 };
        var row = new FieldRow { LabelWidth = 140, MinFieldWidth = 320, ColumnSpacing = 16, RowSpacing = 8, Children = { label, field } };

        Lay(row, 500);
        Assert.Equal(new Rect(156, 0, 344, 30), field.Bounds);

        Lay(row, 400);
        Assert.Equal(new Rect(0, label.Bounds.Height + 8, 400, 30), field.Bounds);
        Assert.Equal(400, label.Bounds.Width);
    });

    // A label with a word wider than the label's column would break that word across lines: the field goes under it,
    // however wide the row, and the label has the row's width.
    [Fact]
    public Task AFieldRowPutsTheFieldUnderALabelWithAWordWiderThanItsColumn() => fx.Ui(() =>
    {
        var label = new TextBlock { Text = "Spracherkennungsmodell", TextWrapping = TextWrapping.Wrap };
        var field = new Border { Height = 30 };
        var row = new FieldRow { LabelWidth = 60, MinFieldWidth = 320, ColumnSpacing = 16, RowSpacing = 8, Children = { label, field } };

        Lay(row, 1000);

        Assert.False(row.IsBeside);
        Assert.Single(label.TextLayout.TextLines);
        Assert.Equal(new Rect(0, label.Bounds.Height + 8, 1000, 30), field.Bounds);
    });

    // Two columns while each has its minimum width, then one; a row is as tall as its tallest field.
    [Fact]
    public Task FormColumnsGoFromTwoColumnsToOne() => fx.Ui(() =>
    {
        Border[] fields = [new() { Height = 20, VerticalAlignment = VerticalAlignment.Top }, new() { Height = 40 }, new() { Height = 20 }];
        var form = new FormColumns { MinColumnWidth = 220, ColumnSpacing = 12, RowSpacing = 12 };
        form.Children.AddRange(fields);

        Lay(form, 500);
        Assert.Equal([new Rect(0, 0, 244, 20), new Rect(256, 0, 244, 40), new Rect(0, 52, 244, 20)], fields.Select(f => f.Bounds));

        Lay(form, 400);
        Assert.Equal([new Rect(0, 0, 400, 20), new Rect(0, 32, 400, 40), new Rect(0, 84, 400, 20)], fields.Select(f => f.Bounds));
    });

    // Layout rounding or DPI scaling can arrange a panel a little narrower than it was measured, right at the width where
    // its layout changes. It keeps the layout it measured, so nothing is placed outside the height it asked for.
    [Fact]
    public Task AFieldRowArrangedJustUnderItsMeasuredWidthKeepsTheMeasuredLayout() => fx.Ui(() =>
    {
        var label = new TextBlock { Text = "Hotkey" };
        var field = new Border { Height = 30 };
        var row = new FieldRow { LabelWidth = 140, MinFieldWidth = 320, ColumnSpacing = 16, RowSpacing = 8, Children = { label, field } };

        row.Measure(new Size(476, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 475, row.DesiredSize.Height));

        Assert.Equal(new Rect(156, 0, 319, 30), field.Bounds);
        Assert.True(field.Bounds.Bottom <= row.DesiredSize.Height, $"{field.Bounds} is below the row's {row.DesiredSize.Height}");
    });

    [Fact]
    public Task FormColumnsArrangedJustUnderTheirMeasuredWidthKeepTheMeasuredColumns() => fx.Ui(() =>
    {
        Border[] fields = [new() { Height = 20, VerticalAlignment = VerticalAlignment.Top }, new() { Height = 40 }];
        var form = new FormColumns { MinColumnWidth = 220, ColumnSpacing = 12, RowSpacing = 12 };
        form.Children.AddRange(fields);

        form.Measure(new Size(452, double.PositiveInfinity));
        form.Arrange(new Rect(0, 0, 451, form.DesiredSize.Height));

        Assert.Equal((0, 0), (fields[0].Bounds.Y, fields[1].Bounds.Y));
        Assert.True(fields[1].Bounds.X > fields[0].Bounds.Right, $"{fields[1].Bounds} is not beside {fields[0].Bounds}");
        Assert.All(fields, f => Assert.True(f.Bounds.Bottom <= form.DesiredSize.Height, $"{f.Bounds} is below the form's {form.DesiredSize.Height}"));
    });

    private static void Lay(Control panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
    }

    [Fact]
    public Task TheRowIconsAreResources() => fx.Ui(() =>
    {
        foreach (var key in RowIcons)
        {
            Assert.True(Application.Current!.TryFindResource(key, out var icon), key);
            Assert.IsAssignableFrom<Geometry>(icon);
        }
    });
}
