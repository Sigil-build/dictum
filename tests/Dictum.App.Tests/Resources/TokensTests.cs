using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Dictum.App.Tests.Resources;

public class TokensTests(AvaloniaFixture fx)
{
    [Fact]
    public Task ColoursFollowTheThemeVariant() => fx.Ui(() =>
    {
        var app = Application.Current!;

        Assert.Equal(Color.Parse("#1C1C1F"), Colour(app, "color.bg.base", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#F3F3F6"), Colour(app, "color.bg.base", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#4C8DFF"), Colour(app, "color.accent", ThemeVariant.Dark));
        Assert.Equal(Color.Parse("#FF5A4E"), Colour(app, "color.state.recording", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#FF5A4E"), Colour(app, "color.state.recording", ThemeVariant.Dark));
    });

    [Fact]
    public Task BothThemesDefineTheSameKeys() => fx.Ui(() =>
    {
        // Compiled XAML may merge the included dictionary itself rather than a ResourceInclude that loads it.
        var tokens = Application.Current!.Resources.MergedDictionaries
            .Select(d => d is ResourceInclude include ? include.Loaded : d)
            .OfType<ResourceDictionary>()
            .Single(d => d.ThemeDictionaries.ContainsKey(ThemeVariant.Light));
        var light = (ResourceDictionary)tokens.ThemeDictionaries[ThemeVariant.Light];
        var dark = (ResourceDictionary)tokens.ThemeDictionaries[ThemeVariant.Dark];

        Assert.Equal(light.Keys.Select(k => (string)k).Order(), dark.Keys.Select(k => (string)k).Order());
        Assert.Equal(27, light.Count);
    });

    [Fact]
    public Task ControlsUseThePlatformFont() => fx.Ui(() =>
    {
        Assert.True(Application.Current!.TryGetResource("ContentControlThemeFontFamily", null, out var font));

        Assert.StartsWith(OperatingSystem.IsWindows() ? "Segoe UI Variable Text" : "Noto Sans", ((FontFamily)font!).Name);
    });

    [Fact]
    public Task SizesAndSpacingAreNumbers() => fx.Ui(() =>
    {
        var app = Application.Current!;

        Assert.True(app.TryGetResource("space.4", null, out var space));
        Assert.True(app.TryGetResource("radius.card", null, out var radius));
        Assert.Equal(16d, space);
        Assert.Equal(new CornerRadius(8), radius);
    });

    private static Color Colour(Application app, string key, ThemeVariant theme)
    {
        Assert.True(app.TryGetResource(key, theme, out var value), key);
        return ((ISolidColorBrush)value!).Color;
    }
}
