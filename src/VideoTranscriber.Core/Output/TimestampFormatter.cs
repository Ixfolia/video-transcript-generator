using System.Globalization;

namespace VideoTranscriber.Output;

/// <summary>Formats timestamps for subtitle files.</summary>
public static class TimestampFormatter
{
    /// <summary>SubRip format, e.g. <c>00:00:01,500</c>.</summary>
    public static string ToSrt(TimeSpan time) => Format(time, ',');

    /// <summary>WebVTT format, e.g. <c>00:00:01.500</c>.</summary>
    public static string ToVtt(TimeSpan time) => Format(time, '.');

    /// <summary>Converts a time to seconds rounded to the millisecond, for machine-readable output.</summary>
    public static double ToSeconds(TimeSpan time) => ToWholeMilliseconds(time) / 1000.0;

    private static string Format(TimeSpan time, char millisecondSeparator)
    {
        long totalMs = ToWholeMilliseconds(time);
        long hours = totalMs / 3_600_000;
        long minutes = totalMs / 60_000 % 60;
        long seconds = totalMs / 1000 % 60;
        long milliseconds = totalMs % 1000;

        // Hours use at least two digits but are never truncated, so a 100+ hour recording stays correct.
        return string.Create(CultureInfo.InvariantCulture,
            $"{hours:00}:{minutes:00}:{seconds:00}{millisecondSeparator}{milliseconds:000}");
    }

    private static long ToWholeMilliseconds(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return 0;
        }

        // Round to the nearest millisecond rather than truncating (TimeSpan has 100 ns resolution).
        return (long)Math.Round(time.Ticks / (double)TimeSpan.TicksPerMillisecond, MidpointRounding.AwayFromZero);
    }
}
