using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Core.Tests.Updates;

/// <summary>The tarball's extraction and its runner as text; nothing runs.</summary>
public sealed class TarballInstallerTests : IDisposable
{
    private const string Lib = "/home/u/.local/lib/dictum";
    private const string Root = "dictum-0.7.1-linux-x64";

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string Updates => _dir.File("updates");

    private string Folder => Path.Combine(Updates, "0.7.1");

    private DownloadedUpdate Update(string root = Root)
    {
        Directory.CreateDirectory(Folder);
        var package = Path.Combine(Folder, "dictum-0.7.1-linux-x64.tar.gz");
        using (var file = File.Create(package))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, root + "/install.sh") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\n")) });
        }

        var offer = new UpdateOffer(new ReleaseVersion(0, 7, 1), new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1"),
            new Installation(InstallKind.Tarball, Lib), new ReleaseAsset("dictum-0.7.1-linux-x64.tar.gz", 1));
        return new DownloadedUpdate(offer, Folder, package);
    }

    private TarballInstaller Installer(FakeLauncher launcher) => new(launcher, new PendingUpdateFile(Updates, _log));

    private static string Script(FakeLauncher launcher)
    {
        var start = Assert.Single(launcher.Started);
        Assert.Equal("/bin/sh", start.FileName);
        return start.ArgumentList[1];
    }

    [Fact]
    public void TheTarballIsExtractedBeforeTheRunnerStarts()
    {
        var launcher = new FakeLauncher { OnStart = () => Assert.True(File.Exists(Path.Combine(Folder, Root, "install.sh"))) };

        Installer(launcher).Start(Update(), 4242, RestartTarget.DesktopApp);

        Assert.Single(launcher.Started);
    }

    [Fact]
    public void TheTarballScriptRunsTheNewInstallSh()
    {
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.DesktopApp);

        var script = Script(launcher);
        int[] order =
        [
            script.IndexOf("while kill -0 4242 2>/dev/null; do sleep 0.2; done", StringComparison.Ordinal),
            script.IndexOf(ShellScript.Quote(Path.Combine(Folder, Root, "install.sh")), StringComparison.Ordinal),
            script.IndexOf($"echo $? > {ShellScript.Quote(Path.Combine(Folder, PendingUpdateFile.ExitCodeFile))}", StringComparison.Ordinal),
            script.IndexOf($"exec '{Lib}/dictum-gui' > /dev/null 2>&1 < /dev/null", StringComparison.Ordinal),
        ];
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Contains($"exec > {ShellScript.Quote(Path.Combine(Folder, "install.log"))} 2>&1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDaemonStartsFromTheInstalledFolder()
    {
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.Daemon);

        Assert.Contains($"exec '{Lib}/dictum' daemon > /dev/null", Script(launcher), StringComparison.Ordinal);
    }

    [Fact]
    public void ATarballWithAnotherRootStartsNothing()
    {
        var launcher = new FakeLauncher();

        var ex = Assert.Throws<UpdateException>(() => Installer(launcher).Start(Update("dictum-0.6.0-linux-x64"), 4242, RestartTarget.DesktopApp));

        Assert.Equal(UpdateFailure.NotVerified, ex.Failure);
        Assert.Empty(launcher.Started);
        Assert.False(File.Exists(Path.Combine(Updates, PendingUpdateFile.Name)));
    }

    [Fact]
    public void AnotherInstallKindIsRefused()
    {
        var update = Update();
        update = update with { Offer = update.Offer with { Installation = new Installation(InstallKind.AppImage, "/x/Dictum.AppImage") } };

        Assert.Equal(UpdateFailure.CannotInstall,
            Assert.Throws<UpdateException>(() => Installer(new FakeLauncher()).Start(update, 4242, RestartTarget.DesktopApp)).Failure);
    }
}
