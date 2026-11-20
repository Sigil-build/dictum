using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Core.Tests.Updates;

/// <summary>The AppImage's swap and its runner as text; nothing runs.</summary>
public sealed class AppImageInstallerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string Updates => _dir.File("updates");

    private string AppImage => _dir.File("Dictum.AppImage");

    private string NewAppImage => _dir.File(".Dictum.AppImage.update-0.7.1");

    private DownloadedUpdate Update(string? appImage = null)
    {
        var folder = Path.Combine(Updates, "0.7.1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(AppImage, "old");
        File.WriteAllText(NewAppImage, "new");
        var offer = new UpdateOffer(new ReleaseVersion(0, 7, 1), new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1"),
            new Installation(InstallKind.AppImage, appImage ?? AppImage), new ReleaseAsset("dictum-0.7.1-linux-x64.AppImage", 3));
        return new DownloadedUpdate(offer, folder, NewAppImage);
    }

    private AppImageInstaller Installer(FakeLauncher launcher) => new(launcher, new PendingUpdateFile(Updates, _log));

    private static string Script(FakeLauncher launcher)
    {
        var start = Assert.Single(launcher.Started);
        Assert.Equal("/bin/sh", start.FileName);
        Assert.Equal("-c", start.ArgumentList[0]);
        return start.ArgumentList[1];
    }

    // The swap is a rename before anything quits (the running copy stays mounted from the old file), so a failed
    // rename leaves the old AppImage and quits nothing.
    [Fact]
    public void TheAppImageIsReplacedBeforeTheRunnerStarts()
    {
        var launcher = new FakeLauncher { OnStart = () => Assert.Equal("new", File.ReadAllText(AppImage)) };

        Installer(launcher).Start(Update(), 4242, RestartTarget.DesktopApp);

        Assert.Single(launcher.Started);
        Assert.False(File.Exists(NewAppImage));
    }

    [Fact]
    public void AFailedRenameStartsNothingAndLeavesNoRecord()
    {
        var launcher = new FakeLauncher();
        var update = Update(_dir.File(Path.Combine("gone", "Dictum.AppImage")));

        var ex = Assert.Throws<UpdateException>(() => Installer(launcher).Start(update, 4242, RestartTarget.DesktopApp));

        Assert.Equal(UpdateFailure.CannotInstall, ex.Failure);
        Assert.Empty(launcher.Started);
        Assert.Equal("old", File.ReadAllText(AppImage));
        Assert.False(File.Exists(Path.Combine(Updates, PendingUpdateFile.Name)));
    }

    [Fact]
    public void TheAppImageScriptWaitsQuitsAndStarts()
    {
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.DesktopApp);

        var script = Script(launcher);
        var appImage = ShellScript.Quote(AppImage);
        int[] order =
        [
            script.IndexOf("while kill -0 4242 2>/dev/null; do sleep 0.2; done", StringComparison.Ordinal),
            script.IndexOf($"{appImage} --quit", StringComparison.Ordinal),
            script.IndexOf($"exec {appImage} > /dev/null 2>&1 < /dev/null", StringComparison.Ordinal),
        ];
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Contains($"exec > {ShellScript.Quote(Path.Combine(Updates, "0.7.1", "install.log"))} 2>&1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDaemonStartsThroughTheAppImagesCli()
    {
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.Daemon);

        Assert.Contains($"exec {ShellScript.Quote(AppImage)} dictum daemon > /dev/null", Script(launcher), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsStartedForRestartTargetNone()
    {
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.None);

        Assert.DoesNotContain("exec " + ShellScript.Quote(AppImage), Script(launcher), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDownloadedAppImageIsMadeExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes");
            return;
        }

        Installer(new FakeLauncher()).Start(Update(), 4242, RestartTarget.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(AppImage));
    }

    [Fact]
    public void AnotherInstallKindIsRefused()
    {
        var update = Update();
        update = update with { Offer = update.Offer with { Installation = new Installation(InstallKind.Msi, AppImage) } };

        Assert.Equal(UpdateFailure.CannotInstall,
            Assert.Throws<UpdateException>(() => Installer(new FakeLauncher()).Start(update, 4242, RestartTarget.DesktopApp)).Failure);
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("/home/a b/Dictum.AppImage", "'/home/a b/Dictum.AppImage'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$HOME `x` \"q\" \\ !", "'$HOME `x` \"q\" \\ !'")]
    [InlineData("", "''")]
    public void ShellQuoteSurvivesQuotesAndSpaces(string value, string quoted) => Assert.Equal(quoted, ShellScript.Quote(value));
}
