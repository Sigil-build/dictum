namespace Dictum.Plugins;

/// <summary>
/// Everything one launch of a plugin needs: who it is, the program in its folder, its model and threads, and where its
/// caches go.
/// </summary>
/// <param name="Model">The <c>--model</c> path; none for a plugin started only to be asked who it is.</param>
/// <param name="Cache">The plugin's folder in Dictum's own folders for what its program caches (<see cref="Core.AppPaths.PluginCacheDirectory"/>).</param>
public sealed record PluginLaunch(string PluginId, string Name, string Version, string Folder, string Executable, string? Model, int? Threads, string Cache);

/// <summary>How long a plugin may take to get ready, how often its health is asked, and how long it has to stop.</summary>
public sealed record PluginTimings(TimeSpan StartTimeout, TimeSpan HealthPoll, TimeSpan StopGrace)
{
    public static PluginTimings Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
}
