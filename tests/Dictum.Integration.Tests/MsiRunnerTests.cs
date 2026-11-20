using System.Diagnostics;
using System.Globalization;
using Dictum.Testing;
using Dictum.Updates;
using Dictum.Updates.Install;

namespace Dictum.Integration.Tests;

/// <summary>
/// The MSI's runner in a real cmd.exe, with batch files standing in for msiexec and for the old dictum-gui; nothing is
/// installed. Windows only.
/// </summary>
public sealed class MsiRunnerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string Updates => _dir.File("updáte ш");

    private string Folder => Path.Combine(Updates, "0.7.1");

    private string Marker => _dir.File("restarted.txt");

    private string InstallerStarted => _dir.File("installer-started.txt");

    /// <summary>
    /// A stand-in for msiexec that writes the time it started and exits with <paramref name="exitCode"/>. The runner calls
    /// msiexec directly, so <c>exit /b</c> gives the runner's line its exit code and lets it go on.
    /// </summary>
    private string FakeMsiexec(int exitCode)
    {
        var path = _dir.File("msiexec.cmd");
        File.WriteAllText(path, $"@echo %TIME%> \"{InstallerStarted}\"\r\n@exit /b {exitCode.ToString(CultureInfo.InvariantCulture)}\r\n");
        return path;
    }

    /// <summary>
    /// A stand-in for the old version. <c>start</c> runs a batch file in <c>cmd /k</c>, so it ends with <c>exit</c>, on the
    /// marker's own line: cmd reads a batch file a line at a time, and once the marker is there the test may delete the
    /// folder. A second line would never be read, and the <c>cmd /k</c> left behind would wait on the test host's stdin and
    /// keep <c>dotnet test</c> from ending.
    /// </summary>
    private string FakeOldVersion()
    {
        var folder = Directory.CreateDirectory(_dir.File("Programs")).FullName;
        File.WriteAllText(Path.Combine(folder, "dictum-gui.cmd"), $"@echo started> \"{Marker}\" & exit\r\n");
        return folder;
    }

    private DownloadedUpdate Update(string target)
    {
        Directory.CreateDirectory(Folder);
        var offer = new UpdateOffer(new ReleaseVersion(0, 7, 1), new Uri("https://github.com/Sigil-build/dictum/releases/tag/v0.7.1"),
            new Installation(InstallKind.Msi, target), new ReleaseAsset("dictum-0.7.1-win-x64.msi", 1));
        return new DownloadedUpdate(offer, Folder, Path.Combine(Folder, "dictum-0.7.1-win-x64.msi"));
    }

    private MsiInstaller Installer(string msiexec) =>
        new(new DetachedRunner(), new PendingUpdateFile(Updates, _log), msiexec, guiFileName: "dictum-gui.cmd");

    private static int Unused() => Environment.ProcessId + 100_000;

    private static async Task Until(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < Patience, "the runner did not get there in time");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private string ExitCode => File.Exists(Path.Combine(Folder, PendingUpdateFile.ExitCodeFile))
        ? File.ReadAllText(Path.Combine(Folder, PendingUpdateFile.ExitCodeFile)).Trim()
        : "";

    [Fact]
    public async Task AFailingInstallerStartsTheOldVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Installer(FakeMsiexec(1603)).Start(Update(FakeOldVersion()), Unused(), RestartTarget.DesktopApp);

        await Until(() => File.Exists(Marker));
        Assert.Equal("1603", ExitCode);
    }

    [Fact]
    public async Task ExitCode3010StartsNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Installer(FakeMsiexec(3010)).Start(Update(FakeOldVersion()), Unused(), RestartTarget.DesktopApp);

        await Until(() => ExitCode.Length > 0);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Equal("3010", ExitCode);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task ASuccessfulInstallStartsNothingItself()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Installer(FakeMsiexec(0)).Start(Update(FakeOldVersion()), Unused(), RestartTarget.DesktopApp);

        await Until(() => ExitCode.Length > 0);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Equal("0", ExitCode);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task TheRunnerWaitsForTheStartingProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        using var starter = Process.Start(new ProcessStartInfo("ping", "-n 4 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;

        Installer(FakeMsiexec(0)).Start(Update(FakeOldVersion()), starter.Id, RestartTarget.None);

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(InstallerStarted), "the installer started while the starting process still ran");
        await starter.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Until(() => ExitCode.Length > 0);
        Assert.True(File.Exists(InstallerStarted));
    }
}
