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
        })
        {
            Assert.Contains(needle, readme);
        }

        Assert.DoesNotContain("## Troubleshooting", readme);
    }
}
