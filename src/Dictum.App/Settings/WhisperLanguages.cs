using System.Globalization;

namespace Dictum.App.Settings;

/// <summary>
/// The languages Whisper recognises (whisper.cpp's list, in its order), with each language's own name from the OS
/// cultures: "Українська", "English". The picker and the chips show these names; the config keeps the codes.
/// </summary>
public static class WhisperLanguages
{
    public static readonly IReadOnlyList<string> Codes =
    [
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi", "fi", "vi",
        "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la", "mi", "ml", "cy", "sk",
        "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq", "sw",
        "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be", "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo",
        "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl", "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su", "yue",
    ];

    /// <summary>The language's own name with a capital letter; the code when the OS has no name for it.</summary>
    public static string Name(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            var native = culture.NativeName;
            return string.IsNullOrEmpty(native) ? code : char.ToUpper(native[0], culture) + native[1..];
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>The first language for a profile that had none: the UI language when Whisper knows it, else English.</summary>
    public static string Fallback()
    {
        var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Codes.Contains(ui) ? ui : "en";
    }
}
