using Dictum.Core.Configuration;

namespace Dictum.App.Settings;

public enum LanguageMode
{
    One,
    Several,
    Auto,
}

/// <summary>
/// A profile's <c>language</c> as the Profiles page shows it: one code, several codes picked per dictation, or auto. The
/// config cannot tell "several" with one code from "one". So <see cref="Parse"/> takes the mode on screen, and the
/// segmented control does not flip while chips are removed.
/// </summary>
public sealed record LanguageSetting(LanguageMode Mode, IReadOnlyList<string> Codes)
{
    /// <param name="shown">The mode on screen. "Several" with one code stays "several", and two or more codes are "several" anyway.</param>
    public static LanguageSetting Parse(string? setting, LanguageMode? shown = null) => LanguageList.Parse(setting) switch
    {
        null or [] => new LanguageSetting(LanguageMode.Auto, []),
        [var one] => new LanguageSetting(shown == LanguageMode.Several ? LanguageMode.Several : LanguageMode.One, [one]),
        var codes => new LanguageSetting(LanguageMode.Several, codes),
    };

    public string Format() => LanguageList.Format(Mode == LanguageMode.Auto ? [] : Codes);

    /// <param name="fallback">The code to start with when there is none yet.</param>
    public LanguageSetting WithMode(LanguageMode mode, string fallback) => mode switch
    {
        LanguageMode.Auto => new LanguageSetting(LanguageMode.Auto, []),
        LanguageMode.One => new LanguageSetting(LanguageMode.One, [Codes.Count > 0 ? Codes[0] : fallback]),
        _ => new LanguageSetting(LanguageMode.Several, Codes.Count > 0 ? Codes : [fallback]),
    };

    /// <summary>"One" replaces its code; "several" adds a new one at the end.</summary>
    public LanguageSetting With(string code) =>
        Mode == LanguageMode.One ? new LanguageSetting(LanguageMode.One, [code])
        : Codes.Contains(code) ? this
        : new LanguageSetting(LanguageMode.Several, [.. Codes, code]);

    /// <summary>The last code stays: a profile always has a language unless it is auto.</summary>
    public LanguageSetting Without(string code) =>
        Codes.Count <= 1 ? this : this with { Codes = [.. Codes.Where(c => c != code)] };
}
