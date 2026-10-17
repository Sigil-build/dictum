using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Resources;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public class ControlsTests(AvaloniaFixture fx)
{
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
        var list = new ListBox { Classes = { "segmented" }, ItemsSource = new[] { "Small", "Medium", "Large" }, SelectedIndex = 1 };
        var window = new Window { Content = list, Width = 600, Height = 200 };
        window.Show();

        var items = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(3, items.Count);
        Assert.True(items[1].Bounds.X > items[0].Bounds.X);
        Assert.Equal(items[0].Bounds.Y, items[1].Bounds.Y);
        window.Close();
    });

    [Fact]
    public Task TheRowIconsAreResources() => fx.Ui(() =>
    {
        foreach (var key in new[] { "icon.login", "icon.microphone", "icon.sounds", "icon.history", "icon.language", "icon.overlay" })
        {
            Assert.True(Application.Current!.TryFindResource(key, out var icon), key);
            Assert.IsAssignableFrom<Geometry>(icon);
        }
    });
}
