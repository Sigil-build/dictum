using System.Globalization;
using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

public sealed class UnitsTests
{
    [Theory]
    [InlineData(874_188_075, "874 MB")]
    [InlineData(1_530_000_000, "1.53 GB")]
    [InlineData(48_000_000_000, "48 GB")]
    [InlineData(885_098, "0.9 MB")]
    [InlineData(0, "0 MB")]
    public void SizesAreDecimalAsModelSitesShowThem(long bytes, string expected) => Assert.Equal(expected, Units.Size(bytes));

    [Fact]
    public void MemoryIsBinaryAsTaskManagerShowsIt() => Assert.Equal("15.8 GB", Units.Memory(16_965_120_819));

    [Theory]
    [InlineData(18_000_000, "18 MB/s")]
    [InlineData(800_000, "0.8 MB/s")]
    public void RatesAreMegabytesPerSecond(double bytesPerSecond, string expected) => Assert.Equal(expected, Units.Rate(bytesPerSecond));

    [Theory]
    [InlineData(6.0, "6 s")]
    [InlineData(0.6, "0.6 s")]
    [InlineData(16.15, "16 s")]
    public void SecondsKeepADecimalUnderTen(double seconds, string expected) => Assert.Equal(expected, Units.Seconds(seconds));

    [Theory]
    [InlineData(32.0, "32 s left")]
    [InlineData(31.2, "32 s left")]
    [InlineData(0.2, "1 s left")]
    [InlineData(250.0, "5 min left")]
    [InlineData(3_900.0, "1 h 5 min left")]
    public void TimeLeftRoundsUp(double seconds, string expected) => Assert.Equal(expected, Units.TimeLeft(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void UkrainianUsesItsDigitsAndUnits()
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("uk");

            Assert.Equal("1,53 ГБ", Units.Size(1_530_000_000));
            Assert.Equal("15,8 ГБ", Units.Memory(16_965_120_819));
            Assert.Equal("ще 5 хв", Units.TimeLeft(TimeSpan.FromSeconds(250)));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
