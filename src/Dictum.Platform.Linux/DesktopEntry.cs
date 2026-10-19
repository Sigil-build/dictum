namespace Dictum.Platform.Linux;

/// <summary>
/// ~/.local/share/applications/dev.dictum.Dictum.desktop, hidden from menus. The portal looks Dictum's app id up
/// there, so the daemon writes it before it registers.
/// </summary>
public static class DesktopEntry
{
    public static string EntryPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
                ? data
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications",
            $"{DBus.Portal.AppId}.desktop");

    public static string Content(LoginItem daemon) => $"""
        [Desktop Entry]
        Type=Application
        Name=Dictum
        Comment=Voice to text
        Exec={daemon.CommandLine}
        Icon=audio-input-microphone
        Terminal=false
        NoDisplay=true
        Categories=Utility;Accessibility;

        """;

    /// <summary>Writes the entry if it is missing or points at another executable.</summary>
    public static async Task WriteAsync(LoginItem daemon, CancellationToken ct)
    {
        var content = Content(daemon);
        if (File.Exists(EntryPath) && await File.ReadAllTextAsync(EntryPath, ct).ConfigureAwait(false) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        await File.WriteAllTextAsync(EntryPath, content, ct).ConfigureAwait(false);
    }
}
