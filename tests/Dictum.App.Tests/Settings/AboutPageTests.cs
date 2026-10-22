using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Setup;

namespace Dictum.App.Tests.Settings;

public sealed class AboutPageTests : IDisposable
{
    private const string System = "Windows 11 (10.0.26200), x64";

    private readonly TempDirectory _dir = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly List<(DocsPage Page, string? Anchor)> _docs = [];
    private readonly List<Uri> _urls = [];
    private Func<Task<HardwareInfo>> _hardware = () => Task.FromResult(new HardwareInfo("NVIDIA GeForce RTX 3060", 0, 12_884_901_888));

    public void Dispose() => _dir.Dispose();

    private AboutPage Page(string? notices = null) => new(new AboutServices(
        "0.4.0", System, "win-x64", notices, () => _hardware(), _clipboard, (page, anchor) => _docs.Add((page, anchor)), _urls.Add,
        NullLog.Instance));

    [Fact]
    public void ThePageShowsTheVersionAsTheRailDoesAndTheSystem()
    {
        var page = Page();

        Assert.Equal((Strings.SettingsAbout, DocsAnchors.Settings.About), (page.Title, page.DocsAnchor));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SettingsVersion), "0.4.0"), page.Version);
        Assert.Equal(System, page.System);
    }

    // For a bug report: the version, the OS and the build, then the GPU Whisper can use and the .NET runtime.
    [Fact]
    public async Task CopyPutsTheVersionTheSystemTheGpuAndTheRuntimeOnTheClipboard()
    {
        var page = Page();

        await page.CopyInfoCommand.ExecuteAsync(null);

        Assert.Equal(
            [$"Dictum 0.4.0 · {System} · win-x64\nGPU: NVIDIA GeForce RTX 3060, 12.0 GB VRAM · {RuntimeInformation.FrameworkDescription}"],
            _clipboard.Texts);
        Assert.Equal(Strings.SettingsAboutCopied, page.CopyStatus);
    }

    [Fact]
    public async Task WithoutAGpuTheLineSaysSo()
    {
        _hardware = () => Task.FromResult(new HardwareInfo(null, 0));
        var page = Page();

        await page.CopyInfoCommand.ExecuteAsync(null);

        Assert.EndsWith($"\nGPU: none · {RuntimeInformation.FrameworkDescription}", _clipboard.Texts.Single(), StringComparison.Ordinal);
    }

    // A broken Vulkan driver must not stop the copy: the GPU is left out.
    [Fact]
    public async Task AProbeThatFailsLeavesTheGpuOut()
    {
        _hardware = () => Task.FromException<HardwareInfo>(new InvalidOperationException("vkCreateInstance failed"));
        var page = Page();

        await page.CopyInfoCommand.ExecuteAsync(null);

        Assert.Equal($"Dictum 0.4.0 · {System} · win-x64\n{RuntimeInformation.FrameworkDescription}", _clipboard.Texts.Single());
    }

    // A busy clipboard throws; a command that throws would take dictum-gui down.
    [Fact]
    public async Task AClipboardThatFailsSaysWhy()
    {
        _clipboard.Fails = new InvalidOperationException("OpenClipboard failed");
        var page = Page();

        await page.CopyInfoCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SettingsAboutCopyFailed), "OpenClipboard failed"), page.CopyStatus);
    }

    [Fact]
    public void DocumentationAndPrivacyOpenTheirPagesOfTheDocs()
    {
        var page = Page();

        page.DocumentationCommand.Execute(null);
        page.PrivacyCommand.Execute(null);

        Assert.Equal([(DocsPage.Home, null), (DocsPage.Privacy, null)], _docs);
    }

    [Fact]
    public void TheProjectLinksOpenGitHub()
    {
        var page = Page();

        page.GitHubCommand.Execute(null);
        page.ReleasesCommand.Execute(null);
        page.ReportProblemCommand.Execute(null);
        page.LicenseCommand.Execute(null);

        Assert.Equal(
        [
            "https://github.com/Sigil-build/dictum",
            "https://github.com/Sigil-build/dictum/releases",
            "https://github.com/Sigil-build/dictum/issues/new/choose",
            "https://github.com/Sigil-build/dictum/blob/HEAD/LICENSE",
        ], _urls.Select(u => u.AbsoluteUri));
    }

    // Every package ships the notices next to the programs; a build from source has none there.
    [Fact]
    public void TheNoticesOpenTheFileNextToTheProgramOrElseTheOneOnGitHub()
    {
        File.WriteAllText(_dir.File(AboutPage.NoticesFileName), "notices");

        Page(AboutPage.Notices(_dir.Path)).NoticesCommand.Execute(null);
        Page(AboutPage.Notices(_dir.File("elsewhere"))).NoticesCommand.Execute(null);

        Assert.Equal(
            [new Uri(_dir.File(AboutPage.NoticesFileName)).AbsoluteUri, "https://github.com/Sigil-build/dictum/blob/HEAD/THIRD-PARTY-NOTICES.txt"],
            _urls.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData(true, "10.0.26200", "Microsoft Windows 10.0.26200", null, Architecture.X64, "Windows 11 (10.0.26200), x64")]
    [InlineData(true, "10.0.19045", "Microsoft Windows 10.0.19045", null, Architecture.X64, "Windows 10 (10.0.19045), x64")]
    [InlineData(false, "6.8.0", "Ubuntu 24.04.1 LTS", "Wayland, KDE", Architecture.X64, "Ubuntu 24.04.1 LTS (Wayland, KDE), x64")]
    [InlineData(false, "6.8.0", "Fedora Linux 41", null, Architecture.Arm64, "Fedora Linux 41, arm64")]
    public void TheSystemIsTheOsTheLinuxSessionAndTheArchitecture(
        bool windows, string version, string description, string? session, Architecture architecture, string expected) =>
        Assert.Equal(expected, AboutPage.SystemText(windows, Version.Parse(version), description, session, architecture));

    // .NET's OS description on Linux is the kernel's; the distribution names itself in os-release.
    [Theory]
    [InlineData("NAME=\"Ubuntu\"|PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"|ID=ubuntu", "Ubuntu 24.04.1 LTS")]
    [InlineData("PRETTY_NAME='Arch Linux'", "Arch Linux")]
    [InlineData("PRETTY_NAME=Gentoo", "Gentoo")]
    [InlineData("NAME=Linux|PRETTY_NAME=\"\"", null)]
    [InlineData("", null)]
    public void TheDistributionIsItsPrettyName(string osRelease, string? expected) =>
        Assert.Equal(expected, AboutPage.Distribution(osRelease.Split('|')));

    private sealed class FakeClipboard : ITextClipboard
    {
        public List<string> Texts { get; } = [];

        public Exception? Fails { get; set; }

        public Task SetTextAsync(string text)
        {
            if (Fails is not null)
            {
                return Task.FromException(Fails);
            }

            Texts.Add(text);
            return Task.CompletedTask;
        }
    }
}
