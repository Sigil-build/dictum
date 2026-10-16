using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Dictum.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The design's system font per platform (tokens font.family.*); Fluent's controls read ContentControlThemeFontFamily.
        if (this.TryGetResource(OperatingSystem.IsWindows() ? "font.family.windows" : "font.family.linux", null, out var font))
        {
            Resources["ContentControlThemeFontFamily"] = font;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
