using System.Globalization;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Setup;

namespace Dictum.App.Tests.Settings;

public sealed class ModelTextTests
{
    private const long Ram = 33_930_241_638;

    private static CatalogEntry Turbo => Catalog.BuiltIn.Get(SetupPlanner.GpuModel);

    [Fact]
    public void TheCardLinesComeFromTheCatalog()
    {
        Assert.Equal("Whisper large-v3-turbo, 8-bit", ModelText.Name(Turbo));
        Assert.Equal("best quality, needs a GPU", ModelText.Note(Turbo));
        Assert.Equal("huggingface.co/ggerganov/whisper.cpp", ModelText.Source(Turbo));
        Assert.Equal("874 MB · MIT licence · huggingface.co/ggerganov/whisper.cpp · 99 languages", ModelText.Meta(Turbo));
        Assert.Equal("English", ModelText.Languages(Catalog.BuiltIn.Get(Catalog.TinyEnglishModel)));
    }

    [Fact]
    public void EverySpeechModelOfTheCatalogHasATranslatedNote()
    {
        // In English most notes are the catalog title's own words, so a model without a case would still pass.
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("uk");
            string[] translated = [Strings.ModelNoteTurbo, Strings.ModelNoteMedium, Strings.ModelNoteSmall, Strings.ModelNoteTiny];

            Assert.All(Catalog.BuiltIn.Entries.Where(e => e.Kind == CatalogKind.Whisper), entry => Assert.Contains(ModelText.Note(entry), translated));
        }
        finally
        {
            CultureInfo.CurrentUICulture = ui;
        }
    }

    [Fact]
    public void AModelOutsideTheListKeepsTheCatalogsWords()
    {
        var catalog = Catalog.Parse("""
            { "version": 1, "entries": [
              { "id": "x", "kind": "whisper", "title": "Whisper x: for y", "file": "x.bin", "url": "https://example.com/a/b/c/x.bin",
                "sha256": "00", "size": 1, "languages": ["uk", "en"], "licence": { "name": "Apache-2.0", "url": "https://example.com" } },
              { "id": "z", "kind": "whisper", "title": "Plain", "file": "z.bin", "url": "not a url",
                "sha256": "00", "size": 1, "languages": ["*"], "licence": { "name": "MIT", "url": "https://example.com" } } ] }
            """);

        Assert.Equal(("Whisper x", "for y", "example.com/a/b", "Українська, English"),
            (ModelText.Name(catalog.Get("x")), ModelText.Note(catalog.Get("x")), ModelText.Source(catalog.Get("x")), ModelText.Languages(catalog.Get("x"))));
        Assert.Equal(("Plain", "", "not a url"), (ModelText.Name(catalog.Get("z")), ModelText.Note(catalog.Get("z")), ModelText.Source(catalog.Get("z"))));
    }

    [Theory]
    [InlineData(1.2, SpeedSource.Dictations, "~6 s for 5 s of speech, from your dictations")]
    [InlineData(1.0, SpeedSource.Measured, "~5 s for 5 s of speech, measured")]
    [InlineData(0.12, SpeedSource.Hardware, "~0.6 s for 5 s of speech (estimate)")]
    [InlineData(3.23, SpeedSource.Scaled, "~16 s for 5 s of speech (estimate)")]
    public void TheSpeedLineNamesItsSource(double ratio, SpeedSource source, string expected) =>
        Assert.Equal(expected, ModelText.Speed(new SpeedEstimate(ratio, source)));

    [Fact]
    public void TheGpuLineNamesTheGpuItsMemoryAndTheRam()
    {
        Assert.Equal("GPU found: Intel(R) Iris(R) Xe Graphics · 15.8 GB shared · 31.6 GB RAM. Recommendations are based on it.",
            ModelText.Hardware(new HardwareInfo("Intel(R) Iris(R) Xe Graphics", 0, 16_965_120_819, GpuIsIntegrated: true), Ram));
        Assert.Equal("GPU found: NVIDIA GeForce RTX 3060 · 12.0 GB VRAM · 31.6 GB RAM. Recommendations are based on it.",
            ModelText.Hardware(new HardwareInfo("NVIDIA GeForce RTX 3060", 0, 12L * 1024 * 1024 * 1024), Ram));
        Assert.Equal("GPU found: GeForce GT 1030 · 1.0 GB VRAM · 31.6 GB RAM. It has under 2 GB, so recommendations are for the CPU.",
            ModelText.Hardware(new HardwareInfo("GeForce GT 1030", 0, 1L * 1024 * 1024 * 1024), Ram));
        Assert.Equal("GPU found: Some GPU · 31.6 GB RAM. Recommendations are based on it.",
            ModelText.Hardware(new HardwareInfo("Some GPU", 0), Ram));
        Assert.Equal("No GPU for Whisper found · 31.6 GB RAM. Recommendations are for the CPU.",
            ModelText.Hardware(new HardwareInfo(null, 0), Ram));
    }

    [Fact]
    public void TheDiskLineSaysWhatModelsUseAndWhatIsFree()
    {
        Assert.Equal("Models use 1.9 GB · 48 GB free", ModelText.Disk(1_900_000_000, 48_000_000_000));
        Assert.Equal("Models use 1.9 GB", ModelText.Disk(1_900_000_000, long.MaxValue));
    }
}
