namespace Dictum.App.Settings;

/// <summary>
/// The speed of one download over its last few seconds, for "18 MB/s · 32 s left" on Settings → Models and in the
/// setup wizard. Another file, or bytes that go back (a restart from zero), start over.
/// </summary>
public sealed class DownloadRate(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();

    /// <summary>The download the samples belong to.</summary>
    public string? Id { get; private set; }

    /// <summary>Bytes per second; null until the samples span a second.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var (firstAt, firstBytes) = _samples.Peek();
            var (lastAt, lastBytes) = _samples.Last();
            var seconds = (lastAt - firstAt).TotalSeconds;
            return seconds < 1 ? null : (lastBytes - firstBytes) / seconds;
        }
    }

    /// <summary>Records the bytes on disk of download <paramref name="id"/> now.</summary>
    public void Add(string id, long bytes)
    {
        if (id != Id || (_samples.Count > 0 && bytes < _samples.Last().Bytes))
        {
            _samples.Clear();
            Id = id;
        }

        var now = time.GetUtcNow();
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 2 && now - _samples.Peek().At > Window)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>How long the rest of <paramref name="total"/> bytes takes at the current rate; null without one.</summary>
    public TimeSpan? TimeLeft(long total) =>
        BytesPerSecond is { } rate && rate > 0
            ? TimeSpan.FromSeconds(Math.Max(0, total - _samples.Last().Bytes) / rate)
            : null;
}
