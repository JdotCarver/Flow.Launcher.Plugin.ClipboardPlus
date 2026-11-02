// ClipboardSubtitleTests.cs
using System.Globalization;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class ClipboardSubtitleTests
{
    [Fact]
    public void RelativeTimestampRetainsPinAndSourceWithoutChangingAbsoluteSubtitle()
    {
        var data = new ClipboardData("synthetic", DataType.PlainText, false)
        {
            HashId = "synthetic", SenderApp = "synthetic.exe", InitScore = 0,
            CreateTime = new DateTime(2026, 11, 2, 10, 0, 0), Pinned = true, Saved = false
        };
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        Assert.Equal("📌now: synthetic.exe", data.GetSubtitleWithTimestamp("now"));
        Assert.Equal($"📌{data.CreateTime.ToString(culture)}: synthetic.exe", data.GetSubtitle(culture));
    }
}
