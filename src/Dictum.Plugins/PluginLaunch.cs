namespace Dictum.Plugins;

/// <summary>Everything one launch of a plugin needs: who it is, the program in its folder, and its model and threads.</summary>
/// <param name="Model">The <c>--model</c> path; none for a plugin started only to be asked who it is.</param>
public sealed record PluginLaunch(string PluginId, string Name, string Version, string Folder, string Executable, string? Model, int? Threads);

/// <summary>How long a plugin may take to get ready, how often its health is asked, and how long it has to stop.</summary>
public sealed record PluginTimings(TimeSpan StartTimeout, TimeSpan HealthPoll, TimeSpan StopGrace)
{
    public static PluginTimings Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
}
