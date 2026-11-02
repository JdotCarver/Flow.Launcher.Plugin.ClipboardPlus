// RelativeTimeFormatter.cs
using System;
using System.Globalization;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;

/// <summary>Formats a clipboard timestamp using a caller-supplied clock and localized labels.</summary>
public static class RelativeTimeFormatter
{
    public static string Format(DateTime timestamp, DateTime now, CultureInfo culture,
        string justNow, Func<int, string> minutesAgo, Func<string, string> today,
        Func<string, string> yesterday, Func<string, string, string> weekday)
    {
        var age = now - timestamp;
        if (age < TimeSpan.FromMinutes(-1))
        {
            return timestamp.ToString("g", culture);
        }
        if (age < TimeSpan.FromMinutes(1))
        {
            return justNow;
        }
        if (age < TimeSpan.FromHours(1))
        {
            return minutesAgo((int)age.TotalMinutes);
        }

        var calendarDays = (now.Date - timestamp.Date).Days;
        var time = timestamp.ToString("t", culture);
        if (calendarDays == 0)
        {
            return today(time);
        }
        if (calendarDays == 1)
        {
            return yesterday(time);
        }
        if (calendarDays < 7)
        {
            return weekday(timestamp.ToString("dddd", culture), time);
        }
        return timestamp.ToString(timestamp.Year == now.Year ? "M" : "d", culture);
    }
}
