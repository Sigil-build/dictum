using Dictum.Testing;

namespace Dictum.Core.Tests.Docs;

public partial class UserDocsTests
{
    // The README is an overview: the guides, the reference and the troubleshooting live on the documentation site.
    [Fact]
    public void ReadmeLinksTheDocumentation()
    {
        var readme = File.ReadAllText(Path.Combine(Repository.Root, "README.md"));

        foreach (var needle in new[]
        {
            "https://docs.sigil.build/dictum/",
            "https://docs.sigil.build/uk/dictum/",
            "https://docs.sigil.build/dictum/getting-started/",
            "https://github.com/Sigil-build/dictum/releases",
            "## License",
            "MIT License",
            "THIRD-PARTY-NOTICES.txt",
            "packaging/package.sh",
            "## Verify your download",
            "SECURITY.md#verify-a-download",
            "https://docs.sigil.build/dictum/getting-started/#check-download",
            "gh attestation verify",
        })
        {
            Assert.Contains(needle, readme);
        }

        Assert.DoesNotContain("## Troubleshooting", readme);

        // The section the README sends a reader to, in both languages.
        Assert.Contains("check-download", Ids("getting-started.md"));
        Assert.Contains("check-download", Ids("getting-started.uk.md"));
    }

    // GitHub sends a reporter to SECURITY.md, and the README sends a reader who wants to check a download there.
    [Fact]
    public void SecurityPolicySaysHowToReportAndToVerify()
    {
        var policy = File.ReadAllText(Path.Combine(Repository.Root, "SECURITY.md"));

        foreach (var needle in new[]
        {
            "Report a vulnerability",
            "https://github.com/Sigil-build/dictum/security",
            "## Verify a download",
            "SHA256SUMS",
            "gh attestation verify",
            "-R Sigil-build/dictum",
            "VirusTotal",
        })
        {
            Assert.Contains(needle, policy);
        }
    }
}
