using System.ComponentModel;
using System.Diagnostics;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Core.Tests.Updates;

/// <summary>The MSI's runner as text, and what is written before it starts; nothing runs.</summary>
public sealed class MsiInstallerTests : IDisposable
{
    private const string Programs = @"C:\Users\u\AppData\Local\Programs\Dictum";

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string Updates => _dir.File("updates");

    private string Folder => Path.Combine(Updates, "0.7.1");

    private DownloadedUpdate Update(string? folder = null, string target = Programs)
    {
        var offer = new UpdateOffer(new ReleaseVersion(0, 7, 1), new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1"),
            new Installation(InstallKind.Msi, target), new ReleaseAsset("dictum-0.7.1-win-x64.msi", 1));
        var at = folder ?? Folder;
        return new DownloadedUpdate(offer, at, Path.Combine(at, "dictum-0.7.1-win-x64.msi"));
    }

    private const string System32 = @"C:\Windows\system32";

    /// <summary>The runner joins paths with the separator of the system it runs on: Windows, for an MSI.</summary>
    private static void SkipOffWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows paths");
        }
    }

    private MsiInstaller Installer(FakeLauncher launcher, string system = System32) =>
        new(launcher, new PendingUpdateFile(Updates, _log), msiexec: null, system: system);

    [Fact]
    public void TheScriptRunsMsiexecPassivelyWithALog()
    {
        SkipOffWindows();
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(@"C:\u\updates\0.7.1"), 4242, RestartTarget.DesktopApp);

        var start = Assert.Single(launcher.Started);
        Assert.Equal(@"C:\Windows\system32\cmd.exe", start.FileName);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.StartsWith("/d /v:on /s /c \"", start.Arguments, StringComparison.Ordinal);
        Assert.Contains(
            "\"C:\\Windows\\system32\\msiexec.exe\" /i \"C:\\u\\updates\\0.7.1\\dictum-0.7.1-win-x64.msi\" /passive /norestart /l*v \"C:\\u\\updates\\0.7.1\\install.log\"",
            start.Arguments, StringComparison.Ordinal);
        Assert.Contains("PID eq 4242", start.Arguments, StringComparison.Ordinal);
        Assert.Contains("> \"C:\\u\\updates\\0.7.1\\exit-code\" echo !rc!", start.Arguments, StringComparison.Ordinal);
        Assert.Contains($"if !rc! neq 0 if !rc! neq 3010 start \"\" /b \"{Programs}\\dictum-gui.exe\"", start.Arguments, StringComparison.Ordinal);
    }

    // The wait comes first, then msiexec, then its exit code, then the old version if it failed.
    [Fact]
    public void TheScriptWaitsInstallsRecordsAndRestartsInThatOrder()
    {
        SkipOffWindows();
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.Daemon);

        var script = launcher.Started[0].Arguments;
        int[] order =
        [
            script.IndexOf("tasklist", StringComparison.Ordinal),
            script.IndexOf("msiexec.exe", StringComparison.Ordinal),
            script.IndexOf("exit-code", StringComparison.Ordinal),
            script.IndexOf($"\"{Programs}\\dictum.exe\" daemon", StringComparison.Ordinal),
        ];
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
    }

    // Git for Windows puts a Unix find on PATH; the wait loop must not pick it up.
    [Fact]
    public void TheWaitLoopNamesTheSystemProgramsInFull()
    {
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.None);

        var script = launcher.Started[0].Arguments;
        Assert.Contains(@"C:\Windows\system32\cmd.exe /d /c", script, StringComparison.Ordinal);
        Assert.Contains(@"C:\Windows\system32\tasklist.exe /fi", script, StringComparison.Ordinal);
        Assert.Contains(@"| C:\Windows\system32\find.exe "" 4242 """, script, StringComparison.Ordinal);
        Assert.Contains(@"C:\Windows\system32\PING.EXE -n 2", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ASystemFolderWithASpaceIsRefused()
    {
        var launcher = new FakeLauncher();

        var ex = Assert.Throws<UpdateException>(() =>
            Installer(launcher, @"C:\My Windows\system32").Start(Update(), 4242, RestartTarget.None));

        Assert.Equal(UpdateFailure.CannotInstall, ex.Failure);
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public void NothingIsRestartedForRestartTargetNone()
    {
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(), 4242, RestartTarget.None);

        Assert.DoesNotContain("neq 3010", launcher.Started[0].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecordIsWrittenBeforeTheRunnerStarts()
    {
        var pendingFile = Path.Combine(Updates, PendingUpdateFile.Name);
        var launcher = new FakeLauncher { OnStart = () => Assert.True(File.Exists(pendingFile)) };

        Installer(launcher).Start(Update(), 4242, RestartTarget.DesktopApp);

        Assert.Single(launcher.Started);
        Assert.Equal(UpdateOutcome.DidNotFinish, new PendingUpdateFile(Updates, _log).Take(new ReleaseVersion(0, 6, 0)).Outcome);
    }

    // A second try at the same version must not be judged by the first try's exit code.
    [Fact]
    public void AnOldExitCodeIsDeletedBeforeTheRunnerStarts()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, PendingUpdateFile.ExitCodeFile), "3010");

        Installer(new FakeLauncher()).Start(Update(), 4242, RestartTarget.DesktopApp);

        Assert.False(File.Exists(Path.Combine(Folder, PendingUpdateFile.ExitCodeFile)));
    }

    [Fact]
    public void ARunnerThatCannotStartLeavesNoRecord()
    {
        var launcher = new FakeLauncher { OnStart = () => throw new Win32Exception(2, "The system cannot find the file specified.") };

        var ex = Assert.Throws<UpdateException>(() => Installer(launcher).Start(Update(), 4242, RestartTarget.DesktopApp));

        Assert.Equal(UpdateFailure.CannotInstall, ex.Failure);
        Assert.False(File.Exists(Path.Combine(Updates, PendingUpdateFile.Name)));
    }

    // A full or read-only data folder: the record cannot be written, so nothing starts, and the update says why.
    [Fact]
    public void ARecordThatCannotBeWrittenStartsNothing()
    {
        File.WriteAllText(Updates, "a file where the updates folder goes");
        var launcher = new FakeLauncher();

        var ex = Assert.Throws<UpdateException>(() => Installer(launcher).Start(Update(), 4242, RestartTarget.DesktopApp));

        Assert.Equal(UpdateFailure.CannotInstall, ex.Failure);
        Assert.Empty(launcher.Started);
    }

    [Theory]
    [InlineData(@"C:\Users\100%\Programs\Dictum")]
    [InlineData(@"C:\Users\Wow!\Programs\Dictum")]
    [InlineData(@"C:\Users\a^b\Programs\Dictum")]
    public void APathCmdWouldChangeIsRefused(string target)
    {
        var launcher = new FakeLauncher();

        var ex = Assert.Throws<UpdateException>(() => Installer(launcher).Start(Update(target: target), 4242, RestartTarget.DesktopApp));

        Assert.Equal(UpdateFailure.CannotInstall, ex.Failure);
        Assert.Empty(launcher.Started);
        Assert.False(File.Exists(Path.Combine(Updates, PendingUpdateFile.Name)));
    }

    // A Cyrillic user name puts Cyrillic letters in every path; the command line is UTF-16, so they pass as they are.
    [Fact]
    public void AUnicodePathIsKept()
    {
        SkipOffWindows();
        var launcher = new FakeLauncher();

        Installer(launcher).Start(Update(target: @"C:\Users\Євген\AppData\Local\Programs\Dictum"), 4242, RestartTarget.DesktopApp);

        Assert.Contains(@"C:\Users\Євген\AppData\Local\Programs\Dictum\dictum-gui.exe", launcher.Started[0].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherKindIsNotItsToInstall()
    {
        var update = Update() with { Offer = Update().Offer with { Installation = new Installation(InstallKind.Tarball, "/home/u/.local/lib/dictum") } };

        Assert.Equal(UpdateFailure.CannotInstall,
            Assert.Throws<UpdateException>(() => Installer(new FakeLauncher()).Start(update, 4242, RestartTarget.DesktopApp)).Failure);
    }
}

/// <summary>Records what an installer would start, and runs <see cref="OnStart"/> instead.</summary>
internal sealed class FakeLauncher : IRunnerLauncher
{
    public List<ProcessStartInfo> Started { get; } = [];

    public Action? OnStart { get; init; }

    public void Start(ProcessStartInfo start)
    {
        OnStart?.Invoke();
        Started.Add(start);
    }
}
