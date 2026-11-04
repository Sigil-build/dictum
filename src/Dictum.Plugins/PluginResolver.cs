using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Text;

namespace Dictum.Plugins;

/// <summary>
/// Turns a <c>"type": "plugin"</c> provider into the launch of its plugin: the folder it was added from, the program
/// its <c>plugin.json</c> names, and the model as a path. Paths are spelt one way, since the plugin host tells
/// processes apart by them.
/// </summary>
/// <param name="registrations">The user's plugins by id, as <c>plugins</c> in config.json lists them.</param>
public sealed class PluginResolver(AppPaths paths, IReadOnlyDictionary<string, PluginRegistration> registrations)
{
    /// <exception cref="PluginUnavailableException">
    /// The plugin is not added, its manifest cannot be used or names another plugin, or the model is not there.
    /// </exception>
    public PluginLaunch Resolve(PluginSttConfig config)
    {
        var id = config.Plugin;
        if (!registrations.TryGetValue(id, out var registration))
        {
            throw new PluginUnavailableException($"Plugin '{id}' is not added.", "Add it with 'dictum plugins add <folder>'.");
        }

        var folder = Normalize(registration.Path);
        PluginManifest manifest;
        try
        {
            manifest = PluginManifest.Load(folder);
        }
        catch (PluginManifestException ex)
        {
            // Quoted, so that the command works when pasted, whatever spaces the folder holds.
            throw new PluginUnavailableException(ex.Message, $"Fix its plugin.json, then confirm it with 'dictum plugins add \"{TerminalText.Show(folder)}\"'.");
        }

        if (manifest.Id != id)
        {
            throw new PluginUnavailableException(
                $"The plugin in {TerminalText.Show(folder)} calls itself '{manifest.Id}', not '{id}'.",
                $"Add it again with 'dictum plugins add \"{TerminalText.Show(folder)}\"'.");
        }

        return new PluginLaunch(
            id, manifest.Name, manifest.Version, folder, manifest.ExecutablePath(folder), Model(config, manifest), config.Threads, paths.PluginCacheDirectory(id));
    }

    /// <summary>
    /// An id from the manifest is a folder under the models folder, where Dictum downloads its files; anything else
    /// is an absolute path to a file or a folder, as the config validator requires.
    /// </summary>
    private string Model(PluginSttConfig config, PluginManifest manifest)
    {
        var (id, model) = (config.Plugin, config.Model);
        if (!PluginIds.IsValidModelId(model))
        {
            var path = Normalize(model);
            return File.Exists(path) || Directory.Exists(path)
                ? path
                : throw new PluginUnavailableException($"Model not found: {TerminalText.Show(path)}", "Check stt.<id>.model in config.json.");
        }

        var models = manifest.Models ?? [];
        if (!models.Any(m => m.Id == model))
        {
            var hint = models.Count == 0
                ? "It lists no models; set the model to an absolute path."
                : $"Its models: {string.Join(", ", models.Select(m => m.Id))}; or set the model to an absolute path.";
            throw new PluginUnavailableException($"Plugin '{id}' has no model '{model}'.", hint);
        }

        var folder = Normalize(Path.Combine(paths.ModelsDirectory, id, model));
        return Directory.Exists(folder)
            ? folder
            : throw new PluginUnavailableException(
                $"Model '{model}' of plugin '{id}' is not in {TerminalText.Show(folder)}.",
                "Put its files there, or set the model to an absolute path.");
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
