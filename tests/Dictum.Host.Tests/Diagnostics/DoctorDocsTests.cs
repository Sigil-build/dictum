using System.Reflection;
using Dictum.Core.Docs;
using Dictum.Host.Diagnostics;
using Dictum.Platform;

namespace Dictum.Host.Tests.Diagnostics;

/// <summary>The section of the troubleshooting page that each doctor row links to.</summary>
public sealed class DoctorDocsTests
{
    /// <summary>Every check name with its section, as the docs plan's table gives it. A new check gets its row here.</summary>
    private static readonly Dictionary<string, string> Sections = new(StringComparer.Ordinal)
    {
        [PlatformCheckNames.Config] = "config",
        [PlatformCheckNames.ConfigWarnings] = "config-warnings",
        [PlatformCheckNames.Models] = "models",
        [PlatformCheckNames.Gpu] = "speech-recognition",
        [PlatformCheckNames.Microphone] = "microphone",
        [PlatformCheckNames.AudioLibrary] = "microphone",
        [PlatformCheckNames.Hotkeys] = "hotkeys",
        [PlatformCheckNames.Paste] = "pasting",
        [PlatformCheckNames.PastePortal] = "pasting",
        [PlatformCheckNames.PasteYdotool] = "pasting",
        [PlatformCheckNames.PasteOrder] = "pasting",
        [PlatformCheckNames.Clipboard] = "pasting",
        [PlatformCheckNames.ActiveWindow] = "pasting",
        [PlatformCheckNames.Elevation] = "administrator",
        [PlatformCheckNames.Secrets] = "secrets",
        [PlatformCheckNames.Session] = "desktop-session",
        [PlatformCheckNames.Platform] = "desktop-session",
        [PlatformCheckNames.Daemon] = "daemon",
        ["stt:local"] = "speech-recognition",
        ["stt:openai"] = "speech-recognition",
        ["processor:llm"] = "llm-cleanup",
        ["something new"] = "doctor",
    };

    public static TheoryData<string, string> Rows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var (check, section) in Sections)
        {
            rows.Add(check, section);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void EachCheckLinksToItsSection(string check, string section) => Assert.Equal(section, DoctorDocs.AnchorFor(check));

    [Fact]
    public void EveryCheckNameHasARow()
    {
        var names = typeof(PlatformCheckNames).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.Name.EndsWith("Prefix", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!);

        Assert.Empty(names.Except(Sections.Keys));
    }

    // The troubleshooting targets are the sections EveryTargetExistsInBothLanguages finds in both languages' pages.
    [Fact]
    public void EverySectionIsATroubleshootingTarget()
    {
        var targets = DocsLinks.Targets.Where(target => target.Page == DocsPage.Troubleshooting).Select(target => target.Anchor);

        Assert.Empty(Sections.Keys.Select(DoctorDocs.AnchorFor).Except(targets));
    }
}
