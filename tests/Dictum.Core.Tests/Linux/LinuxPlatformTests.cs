using Dictum.Platform.Linux;

namespace Dictum.Core.Tests.Linux;

public class LinuxPlatformTests
{
    [Theory]
    [InlineData("wayland", "", "KDE", SessionType.Wayland, Desktop.Kde)]
    [InlineData("", "wayland-0", "ubuntu:GNOME", SessionType.Wayland, Desktop.Gnome)]
    [InlineData("wayland", "wayland-1", "Hyprland", SessionType.Wayland, Desktop.Hyprland)]
    [InlineData("x11", "", "XFCE", SessionType.X11, Desktop.Other)]
    [InlineData("tty", "", "", SessionType.Unknown, Desktop.Other)]
    public void DetectsSessionAndDesktop(string type, string waylandDisplay, string desktop, SessionType expectedType, Desktop expectedDesktop)
    {
        var env = new Dictionary<string, string>
        {
            ["XDG_SESSION_TYPE"] = type,
            ["WAYLAND_DISPLAY"] = waylandDisplay,
            ["XDG_CURRENT_DESKTOP"] = desktop,
        };

        var session = SessionInfo.Detect(name => env.GetValueOrDefault(name));

        Assert.Equal(expectedType, session.Type);
        Assert.Equal(expectedDesktop, session.Desktop);
    }

    [Fact]
    public void SwayIsRecognisedBySwaysock()
    {
        var session = SessionInfo.Detect(name => name switch
        {
            "WAYLAND_DISPLAY" => "wayland-1",
            "SWAYSOCK" => "/run/user/1000/sway-ipc.sock",
            _ => null,
        });

        Assert.Equal(Desktop.Sway, session.Desktop);
        Assert.True(session.IsWlroots);
    }

    [Theory]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x3a00007\n", "0x3a00007")]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x0\n", null)]
    [InlineData("_NET_ACTIVE_WINDOW:  not found.\n", null)]
    public void ParsesActiveWindowId(string output, string? expected) =>
        Assert.Equal(expected, XpropActiveApp.ParseWindowId(output));

    [Theory]
    [InlineData("WM_CLASS(STRING) = \"konsole\", \"konsole\"\n", "konsole")]
    [InlineData("WM_CLASS(STRING) = \"Navigator\", \"Firefox\"\n", "firefox")]
    [InlineData("WM_CLASS:  not found.\n", null)]
    public void ParsesWindowClass(string output, string? expected) =>
        Assert.Equal(expected, XpropActiveApp.ParseWmClass(output));

    [Fact]
    public void UnitFileStartsTheDaemonWithTheGraphicalSession()
    {
        var unit = SystemdAutostart.UnitFile("/home/me/bin/dictum");

        Assert.Contains("ExecStart=\"/home/me/bin/dictum\" daemon", unit);
        Assert.Contains("WantedBy=graphical-session.target", unit);
        Assert.Contains("PartOf=graphical-session.target", unit);
    }

    [Fact]
    public void DesktopEntryIsHiddenAndNamedAfterTheAppId()
    {
        Assert.EndsWith("dev.dictum.Dictum.desktop", DesktopEntry.EntryPath);
        Assert.Contains("NoDisplay=true", DesktopEntry.Content("/opt/dictum/dictum"));
    }
}
