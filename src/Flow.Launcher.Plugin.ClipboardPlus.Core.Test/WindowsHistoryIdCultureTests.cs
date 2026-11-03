// WindowsHistoryIdCultureTests.cs
using System.Globalization;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class WindowsHistoryIdCultureTests
{
    [Fact]
    public void HistoryIdNormalizationIsInvariantUnderTurkishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("synthetic-id", WindowsClipboardHelper.NormalizeHistoryId("SYNTHETIC-ID"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
