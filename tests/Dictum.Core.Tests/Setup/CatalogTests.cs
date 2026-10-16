using System.Text.RegularExpressions;
using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.Core.Tests.Setup;

public partial class CatalogTests
{
    private static readonly Catalog BuiltIn = Catalog.BuiltIn;

    [Fact]
    public void EverySpeechModelHasARelativeCostThatGrowsWithTheModel()
    {
        var speech = BuiltIn.Entries.Where(e => e.Kind == CatalogKind.Whisper).ToList();

        Assert.All(speech, e => Assert.True(e.RelativeCost > 0, $"{e.Id} has no relativeCost"));
        Assert.True(BuiltIn.Get("whisper-small-q8").RelativeCost < BuiltIn.Get("whisper-medium-q8").RelativeCost);
        Assert.True(BuiltIn.Get("whisper-medium-q8").RelativeCost < BuiltIn.Get("whisper-large-v3-turbo-q8").RelativeCost);
        Assert.Null(BuiltIn.Get("vad-silero-6.2").RelativeCost);
    }

    [Fact]
    public void BuiltInCatalogHasSpeechModelsForEveryTierAndOneVadModel()
    {
        Assert.Contains(BuiltIn.Entries, e => e is { Kind: CatalogKind.Whisper, Tier: CatalogTier.Gpu, Hidden: false });
        Assert.Contains(BuiltIn.Entries, e => e is { Kind: CatalogKind.Whisper, Tier: CatalogTier.Cpu, Hidden: false });
        Assert.Single(BuiltIn.Entries, e => e.Kind == CatalogKind.Vad);
    }

    [Fact]
    public void IdsAndFilesAreUnique()
    {
        Assert.Equal(BuiltIn.Entries.Count, BuiltIn.Entries.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(BuiltIn.Entries.Count, BuiltIn.Entries.Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryEntryIsPinnedAndVerifiable()
    {
        Assert.All(BuiltIn.Entries, e =>
        {
            var uri = new Uri(e.Url);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Equal("huggingface.co", uri.Host);
            Assert.Matches(PinnedHuggingFacePath(), uri.AbsolutePath);
            Assert.EndsWith("/" + e.File, uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Matches(Sha256(), e.Sha256);
            Assert.True(e.Size > 0);
            Assert.NotEmpty(e.Languages);
            Assert.False(string.IsNullOrWhiteSpace(e.Licence.Name));
        });
    }

    [Fact]
    public void FindsEntriesByIdAndByFileName()
    {
        Assert.Equal("ggml-small-q8_0.bin", BuiltIn.Get("WHISPER-SMALL-Q8").File);
        Assert.Equal("whisper-small-q8", BuiltIn.ForFile("ggml-small-q8_0.bin")!.Id);
        Assert.Null(BuiltIn.Find("nope"));
        Assert.Null(BuiltIn.ForFile("other.bin"));
    }

    [Fact]
    public void UnknownIdIsAConfigErrorListingVisibleModels()
    {
        var ex = Assert.Throws<ConfigException>(() => BuiltIn.Get("nope"));

        Assert.Contains("whisper-small-q8", ex.Message);
        Assert.DoesNotContain("whisper-tiny-en", ex.Message);
    }

    [GeneratedRegex("^/[^/]+/[^/]+/resolve/[0-9a-f]{40}/[^/]+$")]
    private static partial Regex PinnedHuggingFacePath();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();
}
