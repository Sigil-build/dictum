using Dictum.Core.History;

namespace Dictum.Setup;

/// <summary>Where a speed estimate comes from, best first.</summary>
public enum SpeedSource
{
    /// <summary>The median of the last dictations with the model.</summary>
    Dictations,

    /// <summary>A "Measure" run of the built-in sample.</summary>
    Measured,

    /// <summary>Scaled by <see cref="CatalogEntry.RelativeCost"/> from the nearest model with a known speed.</summary>
    Scaled,

    /// <summary>A fixed anchor for the class of hardware.</summary>
    Hardware,
}

public enum SpeedClass
{
    Fast,
    Usable,
    TooSlow,
}

/// <param name="Ratio">Seconds of work per second of speech: 1.2 means about 6 s for 5 s.</param>
public sealed record SpeedEstimate(double Ratio, SpeedSource Source)
{
    /// <summary>At or under half real time.</summary>
    public const double FastRatio = 0.5;

    /// <summary>At or under 1.5× real time; slower is "Too slow", where "Download anyway" stays.</summary>
    public const double UsableRatio = 1.5;

    public SpeedClass Class => Ratio <= FastRatio ? SpeedClass.Fast : Ratio <= UsableRatio ? SpeedClass.Usable : SpeedClass.TooSlow;

    public double SecondsFor(double speechSeconds) => Ratio * speechSeconds;
}

/// <summary>
/// How long a speech model takes on this computer. Sources, best first:
/// <list type="number">
/// <item>the median of the last <see cref="Dictations"/> dictations with the model;</item>
/// <item>a "Measure" run of the built-in sample;</item>
/// <item>scaling by <see cref="CatalogEntry.RelativeCost"/> from the nearest model with a known speed;</item>
/// <item>a fixed anchor for the class of hardware.</item>
/// </list>
/// </summary>
public static class SpeedEstimator
{
    /// <summary>How many of the latest dictations with a model the median covers.</summary>
    public const int Dictations = 20;

    /// <summary>Turbo on a discrete GPU: about 0.3–1 s for 5 s of speech.</summary>
    public const double DiscreteGpuTurbo = 0.13;

    /// <summary>Turbo on an integrated GPU: about 5–6 s for 5 s, measured on the Iris Xe.</summary>
    public const double IntegratedGpuTurbo = 1.1;

    // Whisper small on the CPU, from the CPU rows of the whisper.cpp benchmark thread (ggml-org/whisper.cpp issue 89):
    // a 30 s window encodes in 1.4–4.8 s on 8 threads (Ryzen 5950X, 3900X, i7-11800H), in 3–6 s on 4 and in 8.7 s on 2
    // (i7-11800H).
    public const double CpuSmallManyCores = 0.6;
    public const double CpuSmallSomeCores = 1.2;
    public const double CpuSmallFewCores = 2.4;

    /// <summary>The relative cost of Whisper large-v3-turbo, which the GPU anchors are for.</summary>
    private const double TurboCost = 35;

    /// <summary>The relative cost of Whisper small, which the CPU anchors are for.</summary>
    private const double SmallCost = 6.5;

    /// <summary>Cores from which <see cref="CpuSmallManyCores"/> applies.</summary>
    private const int ManyCores = 8;

    /// <summary>Cores from which <see cref="CpuSmallSomeCores"/> applies; fewer get <see cref="CpuSmallFewCores"/>.</summary>
    private const int SomeCores = 4;

    private const double MillisecondsPerSecond = 1000.0;

    /// <summary>Seconds of work per second of speech for each model file, from its last <see cref="Dictations"/> dictations.</summary>
    public static IReadOnlyDictionary<string, double> FromHistory(IEnumerable<HistoryEntry> history) =>
        history
            .Where(h => h is { Model: not null, TranscribeMs: not null, AudioSeconds: > 0 })
            .GroupBy(h => Path.GetFileName(h.Model!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => Median([.. g.TakeLast(Dictations).Select(h => h.TranscribeMs.GetValueOrDefault() / MillisecondsPerSecond / h.AudioSeconds)]),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Estimates for the speech models among <paramref name="entries"/>, by catalog id. <paramref name="dictated"/> and
    /// <paramref name="measured"/> are ratios by model file. Until <paramref name="hardware"/> is known, a model with
    /// nothing measured gets no estimate.
    /// </summary>
    public static IReadOnlyDictionary<string, SpeedEstimate> Estimate(
        IEnumerable<CatalogEntry> entries,
        IReadOnlyDictionary<string, double> dictated,
        IReadOnlyDictionary<string, double> measured,
        HardwareInfo? hardware,
        int cpuCores)
    {
        var speech = entries.Where(e => e.Kind == CatalogKind.Whisper).ToList();
        var estimates = new Dictionary<string, SpeedEstimate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in speech)
        {
            if (dictated.TryGetValue(entry.File, out var fromDictations))
            {
                estimates[entry.Id] = new SpeedEstimate(fromDictations, SpeedSource.Dictations);
            }
            else if (measured.TryGetValue(entry.File, out var fromMeasure))
            {
                estimates[entry.Id] = new SpeedEstimate(fromMeasure, SpeedSource.Measured);
            }
        }

        var known = speech.Where(e => e.RelativeCost > 0 && estimates.ContainsKey(e.Id)).ToList();
        foreach (var entry in speech.Where(e => !estimates.ContainsKey(e.Id)))
        {
            if (entry.RelativeCost is not { } cost || cost <= 0)
            {
                continue;
            }

            if (known.MinBy(k => Math.Abs(Math.Log(cost / k.RelativeCost.GetValueOrDefault()))) is { } nearest)
            {
                estimates[entry.Id] = new SpeedEstimate(estimates[nearest.Id].Ratio * cost / nearest.RelativeCost.GetValueOrDefault(), SpeedSource.Scaled);
            }
            else if (hardware is not null)
            {
                var (ratio, anchorCost) = Anchor(hardware, cpuCores);
                estimates[entry.Id] = new SpeedEstimate(ratio * cost / anchorCost, SpeedSource.Hardware);
            }
        }

        return estimates;
    }

    /// <summary>A GPU with too little memory counts as none, as in <see cref="SetupPlanner.RecommendSpeechModel"/>.</summary>
    private static (double Ratio, double Cost) Anchor(HardwareInfo hardware, int cpuCores) =>
        hardware.GpuName is not null && !(hardware.GpuMemoryBytes < SetupPlanner.MinGpuMemoryBytes)
            ? (hardware.GpuIsIntegrated ? IntegratedGpuTurbo : DiscreteGpuTurbo, TurboCost)
            : (cpuCores >= ManyCores ? CpuSmallManyCores : cpuCores >= SomeCores ? CpuSmallSomeCores : CpuSmallFewCores, SmallCost);

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
