using Dictum.Core;
using Dictum.Testing;
using Xunit.Sdk;
using Xunit.v3;

[assembly: AssemblyFixture(typeof(Dictum.Cli.Tests.CliSandbox))]

// The environment and the console are the process's, so these tests take turns.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace Dictum.Cli.Tests;

/// <summary>
/// Points the CLI's config, data, logs and daemon socket into one temp folder for the whole run, so no test reads the
/// real config or reaches the running daemon.
/// </summary>
public sealed class CliSandbox : IDisposable
{
    private static readonly string[] Variables =
    [
        DictumEnvironment.ConfigDirectory, DictumEnvironment.DataDirectory, DictumEnvironment.Socket, DictumEnvironment.ConfigFile,
    ];

    private readonly TempDirectory _dir = new();
    private readonly Dictionary<string, string?> _saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public CliSandbox()
    {
        Environment.SetEnvironmentVariable(DictumEnvironment.ConfigDirectory, ConfigDirectory);
        Environment.SetEnvironmentVariable(DictumEnvironment.DataDirectory, _dir.File("data"));
        Environment.SetEnvironmentVariable(DictumEnvironment.Socket, _dir.File("d.sock"));
        Environment.SetEnvironmentVariable(DictumEnvironment.ConfigFile, null);
    }

    public string ConfigDirectory => _dir.File("config");

    public string File(string name) => _dir.File(name);

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _dir.Dispose();
    }
}
