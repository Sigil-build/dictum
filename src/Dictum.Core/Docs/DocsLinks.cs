using System.Globalization;
using Dictum.Core.Configuration;

namespace Dictum.Core.Docs;

/// <summary>The pages of Dictum's documentation that the programs link to.</summary>
public enum DocsPage
{
    /// <summary><c>dictum/</c>, from <c>docs/index.md</c>.</summary>
    Home,

    /// <summary><c>dictum/reference/settings/</c>, from <c>docs/reference/settings.md</c>.</summary>
    Settings,

    /// <summary><c>dictum/troubleshooting/</c>, from <c>docs/troubleshooting.md</c>.</summary>
    Troubleshooting,

    /// <summary><c>dictum/privacy/</c>, from <c>docs/privacy.md</c>.</summary>
    Privacy,
}

/// <summary>A page, and the id of a section on it, or null for the top of the page.</summary>
public sealed record DocsTarget(DocsPage Page, string? Anchor);

/// <summary>
/// The ids of the sections the programs link to: each is a heading's explicit <c>{#id}</c>, the same in both languages.
/// Released programs keep opening them, so an id never changes, even when its heading is reworded.
/// </summary>
public static class DocsAnchors
{
    /// <summary>On <see cref="DocsPage.Home"/>.</summary>
    public const string HowItWorks = "how-it-works";

    /// <summary>On <see cref="DocsPage.Settings"/>: one section per page of the Settings window.</summary>
    public static class Settings
    {
        public const string General = "general";
        public const string Overlay = "overlay";
        public const string Pasting = "pasting";
        public const string Profiles = "profiles";
        public const string SpeechRecognition = "speech-recognition";
        public const string Processors = "processors";
        public const string Models = "models";
        public const string History = "history";
        public const string System = "system";
        public const string About = "about";
    }

    /// <summary>On <see cref="DocsPage.Troubleshooting"/>: the sections that doctor's rows link to.</summary>
    public static class Troubleshooting
    {
        public const string Doctor = "doctor";
        public const string Config = "config";
        public const string ConfigWarnings = "config-warnings";
        public const string Models = "models";
        public const string Microphone = "microphone";
        public const string Hotkeys = "hotkeys";
        public const string Pasting = "pasting";
        public const string Administrator = "administrator";
        public const string Secrets = "secrets";
        public const string DesktopSession = "desktop-session";
        public const string Daemon = "daemon";
        public const string SpeechRecognition = "speech-recognition";
        public const string LlmCleanup = "llm-cleanup";
    }
}

/// <summary>
/// The URLs of Dictum's documentation on docs.sigil.build: <c>dictum/&lt;page&gt;/#&lt;id&gt;</c> in English and
/// <c>uk/dictum/&lt;page&gt;/#&lt;id&gt;</c> in Ukrainian. The scheme is fixed, as every release opens these URLs.
/// </summary>
public static class DocsLinks
{
    public const string Root = "https://docs.sigil.build/";

    /// <summary>Every section the programs link to; a test finds each one in both languages' pages.</summary>
    public static IReadOnlyList<DocsTarget> Targets { get; } =
    [
        new(DocsPage.Home, null),
        new(DocsPage.Home, DocsAnchors.HowItWorks),
        new(DocsPage.Settings, DocsAnchors.Settings.General),
        new(DocsPage.Settings, DocsAnchors.Settings.Overlay),
        new(DocsPage.Settings, DocsAnchors.Settings.Pasting),
        new(DocsPage.Settings, DocsAnchors.Settings.Profiles),
        new(DocsPage.Settings, DocsAnchors.Settings.SpeechRecognition),
        new(DocsPage.Settings, DocsAnchors.Settings.Processors),
        new(DocsPage.Settings, DocsAnchors.Settings.Models),
        new(DocsPage.Settings, DocsAnchors.Settings.History),
        new(DocsPage.Settings, DocsAnchors.Settings.System),
        new(DocsPage.Settings, DocsAnchors.Settings.About),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Doctor),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Config),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.ConfigWarnings),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Models),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Microphone),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Hotkeys),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Pasting),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Administrator),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Secrets),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.DesktopSession),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Daemon),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.SpeechRecognition),
        new(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.LlmCleanup),
        new(DocsPage.Privacy, null),
    ];

    /// <summary>
    /// The language of the pages to open, <c>en</c> or <c>uk</c>: <paramref name="uiLanguage"/>
    /// (<c>general.uiLanguage</c>) when it is set, else the system's UI language. Any language the docs are not
    /// written in gets English.
    /// </summary>
    public static string Language(string? uiLanguage, CultureInfo osUiCulture)
    {
        ArgumentNullException.ThrowIfNull(osUiCulture);
        var language = uiLanguage ?? osUiCulture.TwoLetterISOLanguageName;
        return language == UiLanguages.Ukrainian ? UiLanguages.Ukrainian : UiLanguages.English;
    }

    /// <summary>The URL of <paramref name="page"/> in <paramref name="language"/>, at the section <paramref name="anchor"/> when it is set.</summary>
    /// <param name="language"><c>en</c> or <c>uk</c>, as <see cref="Language"/> gives it.</param>
    public static Uri For(DocsPage page, string language, string? anchor = null)
    {
        var prefix = language switch
        {
            UiLanguages.English => "",
            UiLanguages.Ukrainian => "uk/",
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "The documentation is in en and uk."),
        };
        var path = page switch
        {
            DocsPage.Home => "dictum/",
            DocsPage.Settings => "dictum/reference/settings/",
            DocsPage.Troubleshooting => "dictum/troubleshooting/",
            DocsPage.Privacy => "dictum/privacy/",
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
        };

        return new Uri(Root + prefix + path + (anchor is null ? "" : "#" + anchor));
    }
}
