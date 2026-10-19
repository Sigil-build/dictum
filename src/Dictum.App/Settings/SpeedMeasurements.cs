namespace Dictum.App.Settings;

/// <summary>Speeds from "Measure" on Settings → Models, by model file, kept while the app runs.</summary>
public sealed class SpeedMeasurements
{
    private readonly Dictionary<string, double> _ratios = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seconds of work per second of speech, by model file name.</summary>
    public IReadOnlyDictionary<string, double> Ratios => _ratios;

    public void Set(string modelFile, double ratio) => _ratios[Path.GetFileName(modelFile)] = ratio;
}
