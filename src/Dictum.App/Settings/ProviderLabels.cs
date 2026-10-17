using System.Globalization;
using Dictum.App.Resources;
using Dictum.Core.Configuration;

namespace Dictum.App.Settings;

/// <summary>How a speech provider reads in a dropdown: "Local Whisper · large-v3-turbo-q8_0", "Remote · home-server:9000".</summary>
public static class ProviderLabels
{
    public static string For(SttConfig config) => config switch
    {
        WhisperLocalSttConfig local => string.Format(CultureInfo.CurrentCulture, Strings.SettingsProviderLocal, ModelName(local.Model)),
        OpenAiCompatibleSttConfig remote => string.Format(CultureInfo.CurrentCulture, Strings.SettingsProviderRemote,
            Uri.TryCreate(remote.BaseUrl, UriKind.Absolute, out var uri) ? uri.Authority : remote.BaseUrl),
        _ => config.GetType().Name,
    };

    private static string ModelName(string model)
    {
        var name = Path.GetFileNameWithoutExtension(model);
        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? name["ggml-".Length..] : name;
    }
}
