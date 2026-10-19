using System.Globalization;
using Dictum.Core;
using Dictum.Core.Configuration;

namespace Dictum.Setup;

public abstract record SetupEvent;

public sealed record DownloadStarted(CatalogEntry Entry) : SetupEvent;

public sealed record DownloadProgressed(DownloadProgress Progress) : SetupEvent;

public sealed record DownloadFinished(CatalogEntry Entry, string Path) : SetupEvent;

public sealed record ConfigSaved(string Path) : SetupEvent;

/// <summary>Downloads what a plan needs, then validates and saves its config.</summary>
public sealed class SetupRunner(FileDownloader downloader, AppPaths paths)
{
    /// <summary>Kept free beyond the downloads themselves, so the system never runs out of disk because of Dictum.</summary>
    public const long SpareBytes = 200_000_000;

    private const double SizeMargin = 1.1;
    private const long WholeMegabytesFrom = 10 * ByteUnits.Megabyte;

    /// <summary>Space the missing files need: their size and a tenth more, with <see cref="SpareBytes"/> left over; 0 when none is missing.</summary>
    public static long NeededBytes(AppPaths paths, IEnumerable<CatalogEntry> entries)
    {
        var missing = entries.Where(e => !ModelFiles.IsInstalled(paths, e)).ToList();
        return missing.Count == 0 ? 0 : NeededFor(missing.Sum(e => e.Size));
    }

    /// <summary>Space a download of <paramref name="bytes"/> needs: a tenth more, with <see cref="SpareBytes"/> left over.</summary>
    internal static long NeededFor(long bytes) => (long)(bytes * SizeMargin) + SpareBytes;

    public async Task DownloadAsync(IReadOnlyList<CatalogEntry> entries, long freeDiskBytes, IProgress<SetupEvent>? progress, CancellationToken ct)
    {
        var missing = entries.Where(e => !ModelFiles.IsInstalled(paths, e)).ToList();
        var needed = NeededBytes(paths, missing);
        if (missing.Count > 0 && freeDiskBytes < needed)
        {
            throw new DownloadException(
                $"Not enough disk space in {paths.ModelsDirectory}: {Format(needed)} needed, {Format(freeDiskBytes)} free.");
        }

        foreach (var entry in missing)
        {
            var target = paths.ResolveModel(entry.File);
            progress?.Report(new DownloadStarted(entry));
            var forward = progress is null ? null : new SyncProgress<DownloadProgress>(p => progress.Report(new DownloadProgressed(p)));
            await downloader.DownloadAsync(entry, target, forward, ct).ConfigureAwait(false);
            progress?.Report(new DownloadFinished(entry, target));
        }
    }

    /// <summary>The config is saved last, so a running daemon only ever reloads a setup whose files are all in place.</summary>
    /// <param name="beforeSave">
    /// Runs after the downloads and validation succeeded, right before the save, e.g. to store an API key the new
    /// config refers to; nothing outside the models directory changes unless the whole setup goes through.
    /// </param>
    public async Task RunAsync(
        SetupPlan plan,
        ConfigStore store,
        long freeDiskBytes,
        IProgress<SetupEvent>? progress,
        CancellationToken ct,
        Func<CancellationToken, Task>? beforeSave = null)
    {
        await DownloadAsync(plan.Downloads, freeDiskBytes, progress, ct).ConfigureAwait(false);
        SetupPlanner.ThrowIfInvalid(plan.Config);
        if (beforeSave is not null)
        {
            await beforeSave(ct).ConfigureAwait(false);
        }

        store.Save(plan.Config);
        progress?.Report(new ConfigSaved(store.Path));
    }

    /// <summary>Decimal units, as model sites show them: "874 MB", "0.9 MB", "2.5 GB".</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= ByteUnits.Gigabyte => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)ByteUnits.Gigabyte:F1} GB"),
        >= WholeMegabytesFrom => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)ByteUnits.Megabyte:F0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)ByteUnits.Megabyte:F1} MB"),
    };
}
