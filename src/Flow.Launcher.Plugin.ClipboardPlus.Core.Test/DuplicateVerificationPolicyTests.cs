// DuplicateVerificationPolicyTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class DuplicateVerificationPolicyTests
{
    private static ClipboardData Record(object data, DataType type, string plain = "") => new(data, type, false)
    {
        HashId = Guid.NewGuid().ToString(), SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1), Pinned = false, Saved = false, PlainText = plain
    };

    [Fact]
    public void BestEffortRespectsTextBoundaryButThoroughDoesNot()
    {
        var shortText = Record(new string('a', 4096), DataType.PlainText);
        var longText = Record(new string('b', 4097), DataType.PlainText);
        Assert.True(DuplicateVerificationPolicy.CanVerifyBestEffort(shortText));
        Assert.False(DuplicateVerificationPolicy.CanVerifyBestEffort(longText));
        Assert.Empty(DuplicateRecordPlanner.Plan(new[] { longText, Record(new string('b', 4097), DataType.PlainText) }, false));
        Assert.Single(DuplicateRecordPlanner.Plan(new[] { longText, Record(new string('b', 4097), DataType.PlainText) }, true));
    }

    [Fact]
    public void RichTextBudgetIncludesPlainTextAndFilesIncludePathSeparators()
    {
        Assert.True(DuplicateVerificationPolicy.CanVerifyBestEffort(Record(new string('x', 4095), DataType.RichText, "x")));
        Assert.False(DuplicateVerificationPolicy.CanVerifyBestEffort(Record(new string('x', 4096), DataType.RichText, "x")));
        Assert.True(DuplicateVerificationPolicy.CanVerifyBestEffort(Record(new[] { new string('x', 4095) }, DataType.Files)));
        Assert.False(DuplicateVerificationPolicy.CanVerifyBestEffort(Record(new[] { new string('x', 4096) }, DataType.Files)));
    }
}
