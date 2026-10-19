using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dictum.App.Shell;

namespace Dictum.App;

public partial class App : Application
{
    /// <summary>The resource Fluent's controls read their font from; controls that draw text themselves read it too.</summary>
    internal const string ContentFontKey = "ContentControlThemeFontFamily";

    /// <summary>Set by Program before Avalonia starts; null in tests and in the self-test, where nothing is started.</summary>
    public static DesktopShell? Shell { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The system font per platform (tokens font.family.*).
        if (this.TryGetResource(OperatingSystem.IsWindows() ? "font.family.windows" : "font.family.linux", null, out var font))
        {
            Resources[ContentFontKey] = font;
        }
    }

    /// <summary>Set by Program for <c>--overlay-demo</c>: it runs once Avalonia has started and calls back with the exit code.</summary>
    public static Action<Action<int>>? Demo { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (Shell is { } shell && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The tray menu is a window: the default mode would quit when it closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            shell.Start(this, code => desktop.Shutdown(code));
        }
        else if (Demo is { } demo && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            demo(code => lifetime.Shutdown(code));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
