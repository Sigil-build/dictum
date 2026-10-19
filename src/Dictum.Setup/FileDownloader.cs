using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Dictum.Setup;

public sealed record DownloadProgress(string File, long Bytes, long Total);

/// <summary>A download failed for good: an HTTP error, network errors after every retry, a wrong hash, or no disk space.</summary>
public sealed class DownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads a catalog file to <c>&lt;target&gt;.partial</c>, resuming with an HTTP Range request after a dropped or
/// stalled connection, and moves it into place only when its SHA-256 matches the catalog. Cancelling keeps the
/// partial file, so the next attempt continues where this one stopped.
/// </summary>
public sealed class FileDownloader(HttpClient http, IReadOnlyList<TimeSpan>? retryDelays = null, TimeSpan? stallTimeout = null)
{
    private const string PartialSuffix = ".partial";
    private const int BufferSize = 1 << 20;
    private const int FirstServerErrorStatus = 500;

    private static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? DefaultRetryDelays;
    private readonly TimeSpan _stallTimeout = stallTimeout ?? DefaultStallTimeout;

    /// <summary>A client for long downloads: no overall timeout (the stall timeout applies instead), redirects followed.</summary>
    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = ConnectionTimeout,
            PooledConnectionLifetime = ConnectionLifetime,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);
        return client;
    }

    public async Task DownloadAsync(CatalogEntry entry, string target, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var partial = PartialPath(target);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await FetchAsync(entry, partial, progress, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (attempt >= _retryDelays.Count)
                {
                    throw new DownloadException($"Could not download {entry.File} from {entry.Url}: {ex.Message}", ex);
                }

                await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false);
            }
        }

        await VerifyAsync(entry, partial, ct).ConfigureAwait(false);
        File.Move(partial, target, overwrite: true);
    }

    /// <summary>Whether the file at <paramref name="path"/> has the catalog's SHA-256; the file is never changed.</summary>
    public static async Task<bool> MatchesAsync(CatalogEntry entry, string path, CancellationToken ct) =>
        string.Equals(await HashAsync(path, ct).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the download of <paramref name="target"/> collects its bytes until the hash is checked.</summary>
    internal static string PartialPath(string target) => target + PartialSuffix;

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException;

    private async Task FetchAsync(CatalogEntry entry, string partial, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > entry.Size)
        {
            File.Delete(partial);
            existing = 0;
        }

        if (existing == entry.Size)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, entry.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        // The stall timer also covers the wait for headers: a server can accept the connection and never answer.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_stallTimeout);
        using var response = await SendAsync(request, stall.Token, ct).ConfigureAwait(false);
        ThrowIfFailed(entry, partial, response);

        var resumeFrom = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent ? existing : 0;
        await CopyAsync(entry, response, partial, resumeFrom, stall, progress, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stalled, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stalled).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Stalled();
        }
    }

    /// <summary>A refused resume and a server error are transient, so they are retried; any other error status is final.</summary>
    private static void ThrowIfFailed(CatalogEntry entry, string partial, HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partial);
            throw new HttpRequestException("the server refused to resume; starting over", null, response.StatusCode);
        }

        if ((int)response.StatusCode >= FirstServerErrorStatus)
        {
            throw new HttpRequestException($"the server answered {(int)response.StatusCode}", null, response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadException($"{entry.Url} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    /// <summary>Appends the body to the partial file when <paramref name="resumeFrom"/> is past 0, otherwise replaces it.</summary>
    private async Task CopyAsync(
        CatalogEntry entry,
        HttpResponseMessage response,
        string partial,
        long resumeFrom,
        CancellationTokenSource stall,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var written = resumeFrom;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(partial, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        var sinceReport = Stopwatch.StartNew();
        progress?.Report(new DownloadProgress(entry.File, written, entry.Size));
        while (true)
        {
            stall.CancelAfter(_stallTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Stalled();
            }

            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (written > entry.Size)
            {
                throw new DownloadException($"{entry.File} is larger than the expected {entry.Size} bytes; the server sent something else.");
            }

            if (sinceReport.Elapsed >= ProgressInterval)
            {
                progress?.Report(new DownloadProgress(entry.File, written, entry.Size));
                sinceReport.Restart();
            }
        }

        progress?.Report(new DownloadProgress(entry.File, written, entry.Size));
        if (written < entry.Size)
        {
            throw new IOException($"the connection closed after {written} of {entry.Size} bytes");
        }
    }

    /// <summary>Transient, so the download is retried and resumes.</summary>
    private IOException Stalled() => new($"no data for {_stallTimeout.TotalSeconds:F0} s");

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task VerifyAsync(CatalogEntry entry, string partial, CancellationToken ct)
    {
        var actual = await HashAsync(partial, ct).ConfigureAwait(false);

        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new DownloadException(
                $"{entry.File} failed the integrity check (SHA-256 {actual}, expected {entry.Sha256}). The download was deleted; run the command again.");
        }
    }
}
