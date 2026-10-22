using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Shell;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the About page uses; fakes in tests.</summary>
/// <param name="Version">The version as the rail shows it, such as "0.4.0".</param>
/// <param name="System">The OS and the architecture, as <see cref="AboutPage.SystemText"/> words them.</param>
/// <param name="RuntimeId">The build's runtime identifier, such as "win-x64".</param>
/// <param name="Notices">THIRD-PARTY-NOTICES.txt next to the program; null where there is none, as in a build from source.</param>
/// <param name="Hardware">The shell's one GPU probe, which Settings → Models shares.</param>
/// <param name="Clipboard">The window's clipboard, for "Copy version and system info".</param>
/// <param name="OpenDocs">Documentation and Privacy.</param>
/// <param name="OpenUrl">GitHub's pages, and the notices file in the app that opens text files.</param>
public sealed record AboutServices(
    string Version,
    string System,
    string RuntimeId,
    string? Notices,
    Func<Task<HardwareInfo>> Hardware,
    ITextClipboard Clipboard,
    OpenDocs OpenDocs,
    Action<Uri> OpenUrl,
    ILog Log);

/// <summary>
/// Settings → About Dictum, the rail's last page; the version at the rail's foot and the tray's "About Dictum" open it.
/// It names the version and the system, links the documentation and the project's pages, the license and the
/// third-party notices, and copies the version and system lines that a bug report asks for.
/// </summary>
public sealed partial class AboutPage(AboutServices services) : SettingsPage
{
    public const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>Windows 11 reports itself as Windows 10.0; its builds start here.</summary>
    private const int Windows11Build = 22000;

    public override string Title => Strings.SettingsAbout;

    public override string DocsAnchor => DocsAnchors.Settings.About;

    /// <summary>"Dictum 0.4.0", as the rail's foot shows it.</summary>
    public string Version => UiText.Format(Strings.SettingsVersion, services.Version);

    public string System => services.System;

    /// <summary>What the last copy did: copied, or why not.</summary>
    [ObservableProperty]
    public partial string? CopyStatus { get; private set; }

    /// <summary>The notices file in <paramref name="directory"/>, the program's folder, or null when it is not there.</summary>
    public static string? Notices(string directory)
    {
        var file = Path.Combine(directory, NoticesFileName);
        return File.Exists(file) ? file : null;
    }

    /// <summary>"Windows 11 (10.0.26200), x64"; on Linux the distribution and the session, "Ubuntu 24.04.1 LTS (Wayland, KDE), x64".</summary>
    /// <param name="osDescription">What .NET calls the OS; on Windows the release is named from the build instead.</param>
    /// <param name="session">The Linux session, as Settings → System names it; null elsewhere.</param>
    public static string SystemText(bool windows, Version osVersion, string osDescription, string? session, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(osVersion);
        var os = windows
            ? $"Windows {(osVersion.Build >= Windows11Build ? 11 : 10)} ({osVersion.Major}.{osVersion.Minor}.{osVersion.Build})"
            : session is null ? osDescription : $"{osDescription} ({session})";
        return $"{os}, {architecture.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// This computer's <see cref="SystemText"/>. On Linux .NET describes the kernel, so the distribution's own name from
    /// os-release stands in for it where there is one.
    /// </summary>
    public static string ThisSystem(DesktopEnvironment desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var description = desktop.IsLinux ? LinuxDistribution() ?? RuntimeInformation.OSDescription : RuntimeInformation.OSDescription;
        return SystemText(desktop.IsWindows, Environment.OSVersion.Version, description, desktop.Session?.ToString(), RuntimeInformation.OSArchitecture);
    }

    /// <summary>The <c>PRETTY_NAME</c> of an os-release file's lines, such as "Ubuntu 24.04.1 LTS"; null when it has none.</summary>
    public static string? Distribution(IEnumerable<string> osRelease)
    {
        ArgumentNullException.ThrowIfNull(osRelease);
        const string Key = "PRETTY_NAME=";
        var line = osRelease.FirstOrDefault(l => l.StartsWith(Key, StringComparison.Ordinal));
        return line?[Key.Length..].Trim().Trim('"', '\'') is { Length: > 0 } name ? name : null;
    }

    /// <summary>os-release's name for this distribution, from where systemd looks for the file.</summary>
    private static string? LinuxDistribution()
    {
        foreach (var file in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                if (File.Exists(file))
                {
                    return Distribution(File.ReadLines(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable file: the kernel's description stays.
            }
        }

        return null;
    }

    [RelayCommand]
    private void Documentation() => services.OpenDocs(DocsPage.Home);

    [RelayCommand]
    private void Privacy() => services.OpenDocs(DocsPage.Privacy);

    [RelayCommand]
    private void GitHub() => services.OpenUrl(ProjectLinks.Repository);

    [RelayCommand]
    private void Releases() => services.OpenUrl(ProjectLinks.Releases);

    [RelayCommand]
    private void ReportProblem() => services.OpenUrl(ProjectLinks.NewIssue);

    [RelayCommand]
    private void License() => services.OpenUrl(ProjectLinks.License);

    /// <summary>The notices of exactly this build when they are next to it, as in every package; GitHub's otherwise.</summary>
    [RelayCommand]
    private void Notices() => services.OpenUrl(services.Notices is { } file ? new Uri(file) : ProjectLinks.Notices);

    [RelayCommand]
    private async Task CopyInfoAsync()
    {
        var text = await InfoAsync();
        try
        {
            await services.Clipboard.SetTextAsync(text);
            CopyStatus = Strings.SettingsAboutCopied;
        }
        catch (Exception ex)
        {
            // A busy clipboard throws; a command that throws would take dictum-gui down.
            CopyStatus = UiText.Format(Strings.SettingsAboutCopyFailed, ex.Message);
            services.Log.Warn($"Version and system info not copied: {ex.Message}");
        }
    }

    /// <summary>
    /// "Dictum 0.4.0 · Windows 11 (10.0.26200), x64 · win-x64", then the GPU Whisper can use and the .NET runtime. In
    /// English whatever the UI's language, as the issue tracker is.
    /// </summary>
    private async Task<string> InfoAsync()
    {
        string? gpu;
        try
        {
            gpu = "GPU: " + (HardwareText.Gpu(await services.Hardware()) ?? "none");
        }
        catch (Exception ex)
        {
            // A broken Vulkan driver must not stop the copy; the line leaves the GPU out.
            services.Log.Warn($"GPU probe failed: {ex.Message}", ex);
            gpu = null;
        }

        var runtime = RuntimeInformation.FrameworkDescription;
        return $"Dictum {services.Version} · {services.System} · {services.RuntimeId}\n{(gpu is null ? runtime : $"{gpu} · {runtime}")}";
    }
}
