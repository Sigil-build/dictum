using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
