using Dictum.App.Resources;
using Dictum.Core.Configuration;

namespace Dictum.App.Settings;

/// <summary>How a speech provider reads in a dropdown: "Local Whisper · large-v3-turbo-q8_0", "Remote · home-server:9000".</summary>
public static class ProviderLabels
{
    private const string GgmlPrefix = "ggml-";

    public static string For(SttConfig config) => config switch
    {
        WhisperLocalSttConfig local => UiText.Format(Strings.SettingsProviderLocal, ModelName(local.Model)),
        OpenAiCompatibleSttConfig remote => UiText.Format(Strings.SettingsProviderRemote,
            Uri.TryCreate(remote.BaseUrl, UriKind.Absolute, out var uri) ? uri.Authority : remote.BaseUrl),
        _ => config.GetType().Name,
    };

    private static string ModelName(string model)
    {
        var name = Path.GetFileNameWithoutExtension(model);
        return name.StartsWith(GgmlPrefix, StringComparison.OrdinalIgnoreCase) ? name[GgmlPrefix.Length..] : name;
    }
}
