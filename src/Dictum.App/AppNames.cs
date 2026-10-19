using Dictum.Platform;

namespace Dictum.App;

/// <summary>The names the desktop app gives itself and looks for: the product, the CLI's executable, the GUI's log.</summary>
internal static class AppNames
{
    /// <summary>The product name in window titles and tray tooltips; a brand, so never translated.</summary>
    public const string Product = "Dictum";

    /// <summary>The CLI's executable and process name, which also runs the daemon.</summary>
    public const string Cli = "dictum";

    public const string GuiLogFile = LoginItem.DesktopAppName + ".log";

    public static string CliFile => OperatingSystem.IsWindows() ? Cli + ".exe" : Cli;
}
