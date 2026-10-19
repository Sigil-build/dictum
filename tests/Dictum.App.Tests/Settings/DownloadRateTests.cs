using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

public sealed class DownloadRateTests
{
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void TheRateFollowsTheLastFiveSeconds()
    {
        var rate = new DownloadRate(_clock);
        rate.Add("a", 0);
        Assert.Null(rate.BytesPerSecond);

        _clock.Advance(TimeSpan.FromSeconds(2));
        rate.Add("a", 4_000_000);
        Assert.Equal(2_000_000, rate.BytesPerSecond.GetValueOrDefault(), 6);
        Assert.Equal(TimeSpan.FromSeconds(3), rate.TimeLeft(10_000_000));

        // Ten more seconds at half the pace: only the last five count.
        for (var i = 1; i <= 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            rate.Add("a", 4_000_000 + (i * 1_000_000));
        }

        Assert.Equal(1_000_000, rate.BytesPerSecond.GetValueOrDefault(), 6);
    }

    [Fact]
    public void AnotherFileOrARestartStartsOver()
    {
        var rate = new DownloadRate(_clock);
        rate.Add("a", 0);
        _clock.Advance(TimeSpan.FromSeconds(2));
        rate.Add("a", 4_000_000);

        rate.Add("b", 100);
        Assert.Equal(("b", (double?)null), (rate.Id, rate.BytesPerSecond));

        _clock.Advance(TimeSpan.FromSeconds(1));
        rate.Add("b", 1_000_100);
        Assert.Equal(1_000_000, rate.BytesPerSecond.GetValueOrDefault(), 6);

        // The server refused to resume: the file starts again from zero.
        rate.Add("b", 50);
        Assert.Null(rate.BytesPerSecond);
        Assert.Null(rate.TimeLeft(2_000_000));
    }

    [Fact]
    public void UnderASecondOfDataGivesNoRate()
    {
        var rate = new DownloadRate(_clock);
        rate.Add("a", 0);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        rate.Add("a", 1_000_000);

        Assert.Null(rate.BytesPerSecond);
    }
}
