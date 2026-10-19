using Dictum.Core.History;
using Dictum.Setup;

namespace Dictum.Core.Tests.Setup;

public class SpeedEstimatorTests
{
    private const string TurboFile = "ggml-large-v3-turbo-q8_0.bin";
    private const string SmallFile = "ggml-small-q8_0.bin";
    private static readonly HardwareInfo IrisXe = new("Intel(R) Iris(R) Xe Graphics", 0, 16_965_120_819, GpuIsIntegrated: true);
    private static readonly HardwareInfo Rtx = new("NVIDIA GeForce RTX 3060", 0, 12L * 1024 * 1024 * 1024);
    private static readonly HardwareInfo SmallGpu = new("GeForce GT 1030", 0, 1L * 1024 * 1024 * 1024);
    private static readonly HardwareInfo CpuOnly = new(null, 0);
    private static readonly Dictionary<string, double> None = [];

    private static IReadOnlyList<CatalogEntry> Entries => Catalog.BuiltIn.Entries;

    private static HistoryEntry Dictation(string model, double audioSeconds, long transcribeMs) =>
        new(DateTimeOffset.UnixEpoch, "work", "local", "raw", "text", "uk", audioSeconds, Model: model, Backend: "vulkan", TranscribeMs: transcribeMs);

    [Fact]
    public void DictationsGiveTheMedianOfTheLastTwenty()
    {
        // Five old, slow dictations fall outside the last 20; one slow outlier inside them does not move the median.
        List<HistoryEntry> history = [.. Enumerable.Range(0, 5).Select(_ => Dictation(TurboFile, 5, 60_000))];
        history.AddRange(Enumerable.Range(0, 19).Select(i => Dictation(TurboFile, 5, 5_000 + (i * 100))));
        history.Add(Dictation(TurboFile, 5, 100_000));
        history.Add(Dictation(SmallFile, 4, 1_000));
        history.Add(new HistoryEntry(DateTimeOffset.UnixEpoch, "work", "remote", "raw", "text", "uk", 5));

        var ratios = SpeedEstimator.FromHistory(history);

        Assert.Equal(2, ratios.Count);
        Assert.Equal(1.19, ratios[TurboFile], 3);
        Assert.Equal(0.25, ratios["GGML-SMALL-Q8_0.BIN"], 3);
    }

    [Fact]
    public void DictationsComeBeforeAMeasure()
    {
        var dictated = new Dictionary<string, double> { [TurboFile] = 1.2 };
        var measured = new Dictionary<string, double> { [TurboFile] = 0.9, [SmallFile] = 0.3 };

        var estimates = SpeedEstimator.Estimate(Entries, dictated, measured, IrisXe, 8);

        Assert.Equal(new SpeedEstimate(1.2, SpeedSource.Dictations), estimates[SetupPlanner.GpuModel]);
        Assert.Equal(new SpeedEstimate(0.3, SpeedSource.Measured), estimates[SetupPlanner.CpuModel]);
    }

    [Fact]
    public void AnUnmeasuredModelIsScaledFromTheNearestKnownOne()
    {
        var dictated = new Dictionary<string, double> { [TurboFile] = 1.1, [SmallFile] = 0.3 };

        var estimates = SpeedEstimator.Estimate(Entries, dictated, None, IrisXe, 8);

        // Medium (cost 19) is nearer turbo (35) than small (6.5) on a log scale.
        Assert.Equal(SpeedSource.Scaled, estimates["whisper-medium-q8"].Source);
        Assert.Equal(1.1 * 19 / 35, estimates["whisper-medium-q8"].Ratio, 6);
        // The hidden English-only tiny (1) is nearest small.
        Assert.Equal(0.3 * 1 / 6.5, estimates["whisper-tiny-en"].Ratio, 6);
        Assert.False(estimates.ContainsKey(SetupPlanner.VadId));
    }

    [Theory]
    [InlineData("rtx", 8, 0.13, SpeedClass.Fast)]
    [InlineData("iris", 8, 1.1, SpeedClass.Usable)]
    [InlineData("cpu", 8, 0.6 * 35 / 6.5, SpeedClass.TooSlow)]
    [InlineData("cpu", 4, 1.2 * 35 / 6.5, SpeedClass.TooSlow)]
    [InlineData("small-gpu", 8, 0.6 * 35 / 6.5, SpeedClass.TooSlow)]
    public void AFreshMachineUsesItsHardwareClass(string machine, int cores, double turbo, SpeedClass turboClass)
    {
        var hardware = machine switch
        {
            "rtx" => Rtx,
            "iris" => IrisXe,
            "small-gpu" => SmallGpu,
            _ => CpuOnly,
        };

        var estimate = SpeedEstimator.Estimate(Entries, None, None, hardware, cores)[SetupPlanner.GpuModel];

        Assert.Equal(SpeedSource.Hardware, estimate.Source);
        Assert.Equal(turbo, estimate.Ratio, 6);
        Assert.Equal(turboClass, estimate.Class);
    }

    [Theory]
    [InlineData(8, 0.6)]
    [InlineData(6, 1.2)]
    [InlineData(2, 2.4)]
    public void TheCpuAnchorIsWhisperSmallByCoreCount(int cores, double small) =>
        Assert.Equal(small, SpeedEstimator.Estimate(Entries, None, None, CpuOnly, cores)[SetupPlanner.CpuModel].Ratio, 6);

    [Fact]
    public void BeforeTheHardwareIsKnownNothingIsGuessed() =>
        Assert.Empty(SpeedEstimator.Estimate(Entries, None, None, hardware: null, 8));

    [Theory]
    [InlineData(0.5, SpeedClass.Fast)]
    [InlineData(0.51, SpeedClass.Usable)]
    [InlineData(1.5, SpeedClass.Usable)]
    [InlineData(1.51, SpeedClass.TooSlow)]
    public void TheClassesFollowTheSpecsThresholds(double ratio, SpeedClass expected) =>
        Assert.Equal(expected, new SpeedEstimate(ratio, SpeedSource.Measured).Class);

    [Fact]
    public void SecondsAreForTheGivenSpeech() => Assert.Equal(6, new SpeedEstimate(1.2, SpeedSource.Dictations).SecondsFor(5), 6);
}
