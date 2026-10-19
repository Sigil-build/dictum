using System.Globalization;
using Dictum.App.Resources;
using Dictum.Setup;

namespace Dictum.App.Settings;

/// <summary>The lines of a model card and of the Models header, in the UI language.</summary>
public static class ModelText
{
    /// <summary>The model's name: the catalog title up to its colon ("Whisper small, 8-bit").</summary>
    public static string Name(CatalogEntry entry) => entry.Title.Split(':', 2)[0].Trim();

    /// <summary>What the model is for: translated for the catalog's models, otherwise the title after its colon.</summary>
    public static string Note(CatalogEntry entry) => entry.Id switch
    {
        SetupPlanner.GpuModel => Strings.ModelNoteTurbo,
        "whisper-medium-q8" => Strings.ModelNoteMedium,
        SetupPlanner.CpuModel => Strings.ModelNoteSmall,
        "whisper-tiny-en" => Strings.ModelNoteTiny,
        _ => entry.Title.Split(':', 2) is [_, var note] ? note.Trim() : "",
    };

    /// <summary>Where the file comes from: the URL's host and repository ("huggingface.co/ggerganov/whisper.cpp").</summary>
    public static string Source(CatalogEntry entry) =>
        Uri.TryCreate(entry.Url, UriKind.Absolute, out var url)
            ? string.Join('/', new[] { url.Host }.Concat(url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2)))
            : entry.Url;

    /// <summary>"99 languages" for a multilingual model, otherwise the languages' own names.</summary>
    public static string Languages(CatalogEntry entry) =>
        entry.Languages.Contains("*") ? Strings.SettingsModelsAllLanguages : string.Join(", ", entry.Languages.Select(WhisperLanguages.Name));

    /// <summary>"874 MB · MIT licence · huggingface.co/ggerganov/whisper.cpp · 99 languages".</summary>
    public static string Meta(CatalogEntry entry) =>
        Format(Strings.SettingsModelsMeta, Units.Size(entry.Size), entry.Licence.Name, Source(entry), Languages(entry));

    /// <summary>"~6 s for 5 s of speech", saying where the number comes from.</summary>
    public static string Speed(SpeedEstimate estimate) => Format(estimate.Source switch
    {
        SpeedSource.Dictations => Strings.SettingsModelsSpeedDictations,
        SpeedSource.Measured => Strings.SettingsModelsSpeedMeasured,
        _ => Strings.SettingsModelsSpeedEstimate,
    }, Units.Seconds(estimate.SecondsFor(5)));

    /// <summary>The GPU and memory line: "15.8 GB shared" for an integrated GPU, "8.0 GB VRAM" for a discrete one.</summary>
    public static string Hardware(HardwareInfo hardware, long memoryBytes)
    {
        var ram = Units.Memory(memoryBytes);
        if (hardware.GpuName is not { } gpu)
        {
            return Format(Strings.SettingsModelsNoGpu, ram);
        }

        if (hardware.GpuMemoryBytes is not { } bytes || bytes <= 0)
        {
            return Format(Strings.SettingsModelsGpuNoMemory, gpu, ram);
        }

        var memory = Format(hardware.GpuIsIntegrated ? Strings.UnitShared : Strings.UnitVram, Units.Memory(bytes));
        return Format(bytes < SetupPlanner.MinGpuMemoryBytes ? Strings.SettingsModelsSmallGpu : Strings.SettingsModelsGpu, gpu, memory, ram);
    }

    /// <summary>The disk line; free space the probe could not read (<see cref="long.MaxValue"/>) is left out.</summary>
    public static string Disk(long usedBytes, long freeBytes) => freeBytes == long.MaxValue
        ? Format(Strings.SettingsModelsDiskUsed, Units.Size(usedBytes))
        : Format(Strings.SettingsModelsDisk, Units.Size(usedBytes), Units.Size(freeBytes));

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
