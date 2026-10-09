using VideoTranscriber.Output;

namespace VideoTranscriber.Tests;

public class TimestampFormatterTests
{
    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(1_500, "00:00:01,500")]
    [InlineData(59_999, "00:00:59,999")]
    [InlineData(60_000, "00:01:00,000")]
    [InlineData(3_599_999, "00:59:59,999")]
    [InlineData(3_600_000, "01:00:00,000")]
    [InlineData(45_296_789, "12:34:56,789")]
    public void ToSrt_FormatsHoursMinutesSecondsAndMillisecondsWithComma(long milliseconds, string expected)
    {
        Assert.Equal(expected, TimestampFormatter.ToSrt(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(1_500, "00:00:01.500")]
    [InlineData(3_723_004, "01:02:03.004")]
    public void ToVtt_UsesDotAsMillisecondSeparator(long milliseconds, string expected)
    {
        Assert.Equal(expected, TimestampFormatter.ToVtt(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void RoundsToNearestMillisecond()
    {
        // 1.4996 s -> 1.500 s, 1.5004 s -> 1.500 s
        Assert.Equal("00:00:01,500", TimestampFormatter.ToSrt(TimeSpan.FromTicks(14_996_000)));
        Assert.Equal("00:00:01,500", TimestampFormatter.ToSrt(TimeSpan.FromTicks(15_004_000)));
    }

    [Fact]
    public void RoundingCarriesIntoSecondsMinutesAndHours()
    {
        TimeSpan almostOneHour = TimeSpan.FromHours(1) - TimeSpan.FromTicks(1);
        Assert.Equal("01:00:00,000", TimestampFormatter.ToSrt(almostOneHour));
    }

    [Fact]
    public void HoursAreNotTruncatedPast99()
    {
        Assert.Equal("123:00:00,000", TimestampFormatter.ToSrt(TimeSpan.FromHours(123)));
    }

    [Fact]
    public void NegativeTimesAreClampedToZero()
    {
        Assert.Equal("00:00:00,000", TimestampFormatter.ToSrt(TimeSpan.FromSeconds(-3)));
        Assert.Equal("00:00:00.000", TimestampFormatter.ToVtt(TimeSpan.FromSeconds(-3)));
    }

    [Theory]
    [InlineData(1_500, 1.5)]
    [InlineData(0, 0.0)]
    [InlineData(61_001, 61.001)]
    public void ToSeconds_RoundsToMilliseconds(long milliseconds, double expected)
    {
        Assert.Equal(expected, TimestampFormatter.ToSeconds(TimeSpan.FromMilliseconds(milliseconds)));
    }
}
