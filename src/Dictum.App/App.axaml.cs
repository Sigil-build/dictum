using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dictum.App.Shell;

namespace Dictum.App;

public partial class App : Application
{
    /// <summary>Set by Program before Avalonia starts; null in tests and in the self-test, where nothing is started.</summary>
    public static DesktopShell? Shell { get; set; }

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
        if (Shell is { } shell && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The tray menu is a window: the default mode would quit when it closes (research §2).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            shell.Start(this, code => desktop.Shutdown(code));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
