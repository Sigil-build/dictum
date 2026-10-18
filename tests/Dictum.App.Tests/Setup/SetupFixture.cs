using Dictum.App.Daemon;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Setup;

namespace Dictum.App.Tests.Setup;

/// <summary>The wizard's world for a test: temp folders, the 4d test catalog (small files), and fakes for the rest.</summary>
internal sealed class SetupFixture : IDisposable
{
    public const string Executable = @"C:\Dictum\dictum-gui.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-setup", Guid.NewGuid().ToString("N")[..8]);

    public SetupFixture() => Directory.CreateDirectory(Paths.ConfigDirectory);

    public AppPaths Paths => new(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"));

    public ConfigStore Store => new(Paths.ConfigFile, NullLog.Instance);

    public FakeDownloads Downloads { get; } = new();

    public FakeProbe Probe { get; } = new();

    public FakeSecrets Secrets { get; } = new();

    public FakeAutostart Autostart { get; } = new();

    public ManualTimers Timers { get; } = new();

    public ManualClock Clock { get; } = new(DateTimeOffset.UnixEpoch);

    public SpeedMeasurements Measurements { get; } = new();

    public HardwareInfo Hardware { get; set; } = ModelsPageTests.IrisXe;

    /// <summary>A probe that has not answered yet; null answers at once with <see cref="Hardware"/>.</summary>
    public TaskCompletionSource<HardwareInfo>? Probing { get; set; }

    public long FreeDisk { get; set; } = 48_000_000_000;

    public bool HasAutostart { get; set; } = true;

    public TrayDesktop Desktop { get; set; } = TrayDesktop.Windows;

    public bool? TrayHidden { get; set; } = true;

    public int TraySettingsOpened { get; private set; }

    /// <summary>The daemon as step 8 sees it; not set up until a test says otherwise.</summary>
    public FakeChannel Daemon { get; } = new() { Link = new DaemonLink(DaemonLinkState.NotConfigured) };

    public int Restarts { get; private set; }

    public SetupServices Services() => new(
        Paths, Store, ModelsPageTests.TestCatalog(), Downloads,
        () => Probing?.Task ?? Task.FromResult(Hardware), () => FreeDisk, ModelsPageTests.Memory, 8, Measurements, Probe, Secrets,
        HasAutostart ? Autostart : null, HasAutostart ? Executable : null,
        new TrayHint(Desktop, () => TrayHidden, Desktop == TrayDesktop.Windows ? () => TraySettingsOpened++ : null),
        Daemon, () => Restarts++,
        Timers, Clock, NullLog.Instance);

    public void Install(string file, byte[] data)
    {
        Directory.CreateDirectory(Paths.ModelsDirectory);
        File.WriteAllBytes(ModelPath(file), data);
    }

    /// <summary>The 4d test catalog's speech model files and the VAD file, all in place.</summary>
    public void InstallAll()
    {
        Install(ModelsPageTests.TurboFile, ModelsPageTests.TurboData);
        Install("ggml-silero-v6.2.0.bin", [1, 2, 3]);
    }

    public string ModelPath(string file) => Path.Combine(Paths.ModelsDirectory, file);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>"Test connection" answered by the test; <see cref="Held"/> answers later, in order.</summary>
internal sealed class FakeProbe : IServerProbe
{
    public List<(string Url, string Model, string? Key)> Calls { get; } = [];

    public ServerCheck Answer { get; set; } = new(true, "Reachable · 12 ms");

    public Queue<TaskCompletionSource<ServerCheck>> Held { get; } = new();

    /// <summary>Thrown by the next checks, as a keyring or socket error the probe did not expect.</summary>
    public Exception? Throws { get; set; }

    public Task<ServerCheck> CheckAsync(string baseUrl, string model, string? apiKey, CancellationToken ct)
    {
        Calls.Add((baseUrl, model, apiKey));
        if (Throws is not null)
        {
            return Task.FromException<ServerCheck>(Throws);
        }

        return Held.TryDequeue(out var held) ? held.Task : Task.FromResult(Answer);
    }
}
