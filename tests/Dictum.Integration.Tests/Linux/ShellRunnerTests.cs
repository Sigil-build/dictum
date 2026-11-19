using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Integration.Tests.Linux;

/// <summary>
/// The AppImage's and the tarball's runners in a real sh under setsid, with shell scripts standing in for the AppImage,
/// for install.sh and for the installed programs; they log their calls. Linux only.
/// </summary>
public sealed class ShellRunnerTests : IDisposable
{
    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string Updates => _dir.File("updates");

    private string Folder => Directory.CreateDirectory(Path.Combine(Updates, "0.7.1")).FullName;

    private string Calls => _dir.File("calls.log");

    private string CallLog => File.Exists(Calls) ? File.ReadAllText(Calls) : "";

    private string Logger(string who) => $"#!/bin/sh\necho \"{who} $*\" >> '{Calls}'\n";

    [UnsupportedOSPlatform("windows")]
    private static void WriteScript(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, Executable);
    }

    private static UpdateOffer Offer(InstallKind kind, string target, string package) =>
        new(new ReleaseVersion(0, 7, 1), new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1"),
            new Installation(kind, target), new ReleaseAsset(package, 1));

    /// <summary>A pid that does not run: the runner goes on at once.</summary>
    private static int Gone()
    {
        using var done = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        done.WaitForExit();
        return done.Id;
    }

    [Fact]
    public async Task TheShellRunnerReplacesAndStartsAfterThePidExits()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only");
            return;
        }

        var appImage = _dir.File("Dictum App's.AppImage");
        var downloaded = _dir.File(".Dictum App's.AppImage.update-0.7.1");
        WriteScript(appImage, Logger("old"));
        File.WriteAllText(downloaded, Logger("new"));
        using var starter = Process.Start(new ProcessStartInfo("sleep", "2") { UseShellExecute = false })!;
        var update = new DownloadedUpdate(Offer(InstallKind.AppImage, appImage, "dictum-0.7.1-linux-x64.AppImage"), Folder, downloaded);

        new AppImageInstaller(new DetachedRunner(), new PendingUpdateFile(Updates, _log)).Start(update, starter.Id, RestartTarget.DesktopApp);

        Assert.Equal(Logger("new"), File.ReadAllText(appImage));
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Equal("", CallLog);
        await starter.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Eventually.WaitForAsync(() => CallLog.Split('\n').Length >= 3, "the runner's two calls", Patience);
        Assert.Equal("new --quit\nnew \n", CallLog);
    }

    [Fact]
    public async Task AFailingInstallShStillStartsWhatIsInstalled()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only");
            return;
        }

        var lib = Directory.CreateDirectory(_dir.File("lib dictum")).FullName;
        WriteScript(Path.Combine(lib, "dictum"), Logger("installed dictum"));
        var package = Path.Combine(Folder, "dictum-0.7.1-linux-x64.tar.gz");
        using (var file = File.Create(package))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dictum-0.7.1-linux-x64/install.sh")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(Logger("install.sh") + "echo 'install.sh failed' >&2\nexit 3\n")),
                Mode = Executable,
            });
        }

        var update = new DownloadedUpdate(Offer(InstallKind.Tarball, lib, "dictum-0.7.1-linux-x64.tar.gz"), Folder, package);

        new TarballInstaller(new DetachedRunner(), new PendingUpdateFile(Updates, _log)).Start(update, Gone(), RestartTarget.Daemon);

        await Eventually.WaitForAsync(() => CallLog.Contains("installed dictum", StringComparison.Ordinal), "the installed daemon", Patience);
        Assert.Equal("install.sh \ninstalled dictum daemon\n", CallLog);
        Assert.Equal("3", File.ReadAllText(Path.Combine(Folder, PendingUpdateFile.ExitCodeFile)).Trim());
        Assert.Contains("install.sh failed", File.ReadAllText(Path.Combine(Folder, "install.log")), StringComparison.Ordinal);
    }
}
