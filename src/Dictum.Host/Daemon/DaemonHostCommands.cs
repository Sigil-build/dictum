using Dictum.Core.Configuration;
using Dictum.Platform;

namespace Dictum.Host.Daemon;

/// <summary>The commands the daemon answers with what its host owns: the config file and the paths.</summary>
/// <param name="Reload">Reads the config file again, for <c>reload</c>.</param>
/// <param name="Doctor">Runs the checks on the daemon it is given, for <c>doctor</c>.</param>
public sealed record DaemonHostCommands(
    Func<(DictumConfig Config, IReadOnlyList<ConfigIssue> Warnings)> Reload,
    Func<DictumDaemon, CancellationToken, Task<IReadOnlyList<PlatformCheck>>> Doctor);
