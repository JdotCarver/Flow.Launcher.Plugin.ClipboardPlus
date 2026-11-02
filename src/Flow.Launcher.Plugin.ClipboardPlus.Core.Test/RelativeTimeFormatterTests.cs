// RelativeTimeFormatterTests.cs
using System.Globalization;
using Xunit;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class RelativeTimeFormatterTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0);
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("fr-FR");

    private static string Format(DateTime timestamp) => RelativeTimeFormatter.Format(
        timestamp, Now, Culture, "now", n => $"{n} minutes", t => $"today {t}",
        t => $"yesterday {t}", (d, t) => $"{d} {t}");

    [Fact]
    public void FormatsElapsedBoundariesAndFutureTimestamps()
    {
        Assert.Equal("now", Format(Now.AddSeconds(-59)));
        Assert.Equal("1 minutes", Format(Now.AddMinutes(-1)));
        Assert.Equal("59 minutes", Format(Now.AddSeconds(-3599)));
        Assert.Equal("today 11:00", Format(Now.AddHours(-1)));
        Assert.Equal("now", Format(Now.AddMinutes(1)));
        Assert.Equal(Now.AddSeconds(61).ToString("g", Culture), Format(Now.AddSeconds(61)));
    }

    [Fact]
    public void UsesCalendarDaysAndCultureForOlderDates()
    {
        Assert.StartsWith("yesterday ", Format(Now.Date.AddSeconds(-1)));
        var sixDays = Now.Date.AddDays(-6);
        Assert.Equal($"{sixDays.ToString("dddd", Culture)} {sixDays.ToString("t", Culture)}", Format(sixDays));
        Assert.Equal(Now.AddDays(-7).ToString("M", Culture), Format(Now.AddDays(-7)));
        var lastYear = new DateTime(2025, 12, 31);
        Assert.Equal(lastYear.ToString("d", Culture), Format(lastYear));
    }
}
