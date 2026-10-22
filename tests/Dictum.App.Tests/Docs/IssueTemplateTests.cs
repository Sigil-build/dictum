using System.Globalization;
using Dictum.App.Resources;
using Dictum.Core.Docs;

namespace Dictum.App.Tests.Docs;

/// <summary>
/// The issue forms in <c>.github/ISSUE_TEMPLATE/</c>: the bug report asks for what About Dictum copies, under the names
/// the app shows in English, and points to the logs; the chooser links the documentation and the private security report.
/// </summary>
public sealed class IssueTemplateTests
{
    private static string Form(string name) => File.ReadAllText(Path.Combine(Repository.Root, ".github", "ISSUE_TEMPLATE", name));

    private static string English(string key) => Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)!;

    [Fact]
    public void TheBugReportAsksForWhatAboutDictumCopies()
    {
        var form = Form("bug_report.yml");

        Assert.Contains($"**{English(nameof(Strings.SettingsAbout))}**", form, StringComparison.Ordinal);
        Assert.Contains($"**{English(nameof(Strings.SettingsAboutCopy))}**", form, StringComparison.Ordinal);
        Assert.Contains($"**{English(nameof(Strings.MenuAbout))}**", form, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBugReportLinksTheLogsSectionOfTroubleshooting()
    {
        const string Logs = "logs";

        Assert.Contains(DocsLinks.For(DocsPage.Troubleshooting, "en", Logs).AbsoluteUri, Form("bug_report.yml"), StringComparison.Ordinal);
        Assert.Contains($"{{#{Logs}}}", File.ReadAllText(Path.Combine(Repository.Root, "docs", "troubleshooting.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheChooserLinksTheDocumentationAndThePrivateSecurityReport()
    {
        var config = Form("config.yml");

        Assert.Contains(DocsLinks.For(DocsPage.Home, "en").AbsoluteUri, config, StringComparison.Ordinal);
        Assert.Contains("https://github.com/Sigil-build/dictum/security/advisories/new", config, StringComparison.Ordinal);
    }
}
