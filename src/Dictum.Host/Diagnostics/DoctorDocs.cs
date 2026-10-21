using Dictum.Core.Docs;
using Dictum.Platform;

namespace Dictum.Host.Diagnostics;

/// <summary>The section of the troubleshooting page that explains a doctor row, for its link.</summary>
public static class DoctorDocs
{
    /// <summary>
    /// The id of the troubleshooting section for the check <paramref name="checkName"/>: one of
    /// <see cref="DocsAnchors.Troubleshooting"/>, and its <c>doctor</c> section for a check the page has no section for.
    /// </summary>
    public static string AnchorFor(string checkName)
    {
        ArgumentNullException.ThrowIfNull(checkName);
        if (checkName.StartsWith(PlatformCheckNames.SttPrefix, StringComparison.Ordinal))
        {
            return DocsAnchors.Troubleshooting.SpeechRecognition;
        }

        if (checkName.StartsWith(PlatformCheckNames.ProcessorPrefix, StringComparison.Ordinal))
        {
            return DocsAnchors.Troubleshooting.LlmCleanup;
        }

        return checkName switch
        {
            PlatformCheckNames.Config => DocsAnchors.Troubleshooting.Config,
            PlatformCheckNames.ConfigWarnings => DocsAnchors.Troubleshooting.ConfigWarnings,
            PlatformCheckNames.Models => DocsAnchors.Troubleshooting.Models,
            PlatformCheckNames.Gpu => DocsAnchors.Troubleshooting.SpeechRecognition,
            PlatformCheckNames.Microphone or PlatformCheckNames.AudioLibrary => DocsAnchors.Troubleshooting.Microphone,
            PlatformCheckNames.Hotkeys => DocsAnchors.Troubleshooting.Hotkeys,
            PlatformCheckNames.Paste or PlatformCheckNames.PastePortal or PlatformCheckNames.PasteYdotool
                or PlatformCheckNames.PasteOrder or PlatformCheckNames.Clipboard or PlatformCheckNames.ActiveWindow
                => DocsAnchors.Troubleshooting.Pasting,
            PlatformCheckNames.Elevation => DocsAnchors.Troubleshooting.Administrator,
            PlatformCheckNames.Secrets => DocsAnchors.Troubleshooting.Secrets,
            PlatformCheckNames.Session or PlatformCheckNames.Platform => DocsAnchors.Troubleshooting.DesktopSession,
            PlatformCheckNames.Daemon => DocsAnchors.Troubleshooting.Daemon,
            _ => DocsAnchors.Troubleshooting.Doctor,
        };
    }
}
