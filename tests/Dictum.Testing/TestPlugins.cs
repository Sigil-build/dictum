using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Dictum.Plugins;
using Dictum.TestPlugin;

namespace Dictum.Testing;

/// <summary>Plugin folders made from the built test plugin (<c>tests/Dictum.TestPlugin</c>), for tests that launch it.</summary>
public static class TestPlugins
{
    /// <summary>The test plugin's own code, which its program, the .NET launcher, loads: what a manifest's <c>program</c> would list.</summary>
    public const string AssemblyFile = ProgramName + ".dll";

    private const string ProgramName = "dictum-test-plugin";
    private const string BehaviourFile = "behaviour.json";

    /// <summary>The test plugin that the project reference built, in the same configuration as this test.</summary>
    public static string BinDirectory
    {
        get
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            return Path.Combine(Repository.Root, "tests", "Dictum.TestPlugin", "bin", configuration, "net10.0");
        }
    }

    private static string ProgramFile => OperatingSystem.IsWindows() ? ProgramName + ".exe" : ProgramName;

    /// <summary>
    /// A copy of the test plugin in <paramref name="dir"/>, under <paramref name="folderName"/> (else the id), with a
    /// <c>plugin.json</c> for this system, listing <paramref name="program"/> when given, and, when given, the
    /// behaviour as <c>behaviour.json</c>.
    /// </summary>
    public static string Folder(
        TempDirectory dir,
        string id = "test",
        string? folderName = null,
        TestPluginBehaviour? behaviour = null,
        IReadOnlyList<string>? program = null)
    {
        var folder = Path.Combine(dir.Path, folderName ?? id);
        CopyDirectory(BinDirectory, folder);

        var manifest = new JsonObject
        {
            ["protocol"] = PluginProtocol.Version,
            ["id"] = id,
            ["name"] = "Test",
            ["version"] = "1.0.0",
            ["executable"] = new JsonObject { [PluginProtocol.CurrentRid] = ProgramFile },
        };
        if (program is not null)
        {
            manifest["program"] = new JsonArray([.. program.Select(entry => JsonValue.Create(entry))]);
        }

        File.WriteAllText(Path.Combine(folder, PluginProtocol.ManifestFile), manifest.ToJsonString());
        behaviour?.Save(Path.Combine(folder, BehaviourFile));

        if (!OperatingSystem.IsWindows())
        {
            var executable = Path.Combine(folder, ProgramFile);
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return folder;
    }

    /// <summary>A behaviour file to pass as the <c>--model</c>, which the test plugin prefers to <c>behaviour.json</c>.</summary>
    public static string Model(TempDirectory dir, TestPluginBehaviour behaviour, string name = "model.json")
    {
        var path = dir.File(name);
        behaviour.Save(path);
        return path;
    }

    /// <summary>A launch of the plugin in <paramref name="folder"/>, its name, version and program read from its manifest.</summary>
    public static PluginLaunch Launch(string folder, string? model = null, int? threads = null, string id = "test")
    {
        var manifest = PluginManifest.Load(folder);
        return new PluginLaunch(id, manifest.Name, manifest.Version, folder, manifest.ExecutablePath(folder), model, threads);
    }

    /// <summary>Whether a test plugin copied into <paramref name="dir"/> still runs; every test copies the plugin into its own.</summary>
    public static bool RunsIn(TempDirectory dir)
    {
        var running = RunningIn(dir);
        foreach (var process in running)
        {
            process.Dispose();
        }

        return running.Count > 0;
    }

    /// <summary>Kills every test plugin still running from <paramref name="dir"/>, such as a child a test left on purpose.</summary>
    public static void EndAllIn(TempDirectory dir)
    {
        foreach (var process in RunningIn(dir))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // It exited meanwhile.
                }
            }
        }
    }

    /// <summary>
    /// The process that <see cref="TestPluginBehaviour.LeaveChild"/> started from <paramref name="folder"/>, once its pid
    /// is written; null when it has ended already. The caller disposes it.
    /// </summary>
    public static async Task<Process?> LeftChildAsync(string folder, TimeSpan patience)
    {
        var file = Path.Combine(folder, TestPluginBehaviour.ChildPidFile);
        await Eventually.WaitForAsync(() => File.Exists(file), "the pid of the plugin's child", patience);
        Process process;
        try
        {
            process = Process.GetProcessById(int.Parse(File.ReadAllText(file), CultureInfo.InvariantCulture));
        }
        catch (ArgumentException)
        {
            // No process has that id any more.
            return null;
        }

        try
        {
            // Not another program that got the id once the child was gone.
            if (!process.HasExited && process.ProcessName == ProgramName)
            {
                return process;
            }
        }
        catch (InvalidOperationException)
        {
            // It exited while we looked.
        }

        process.Dispose();
        return null;
    }

    /// <summary>The test plugins running from <paramref name="dir"/>; the caller disposes them.</summary>
    private static List<Process> RunningIn(TempDirectory dir)
    {
        var mine = Path.DirectorySeparatorChar + Path.GetFileName(dir.Path) + Path.DirectorySeparatorChar;
        var running = new List<Process>();
        foreach (var process in Process.GetProcessesByName(ProgramName))
        {
            try
            {
                if (!process.HasExited && process.MainModule?.FileName.Contains(mine, StringComparison.OrdinalIgnoreCase) == true)
                {
                    running.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // It exited while we looked.
            }

            process.Dispose();
        }

        return running;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }
}
