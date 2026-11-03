// WindowsImportPinPolicyTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class WindowsImportPinPolicyTests
{
    private static ClipboardData Item(string id, bool pinned) => new("synthetic", DataType.PlainText, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1), Pinned = pinned, Saved = false
    };

    [Fact]
    public void TrustedImportKeepsRealPinsAndUnpinnedItemsUnchanged()
    {
        var items = new[] { Item("one", false), Item("two", false), Item("three", true) };
        Assert.False(WindowsImportPinPolicy.ProtectUnverified(items, true, true));
        Assert.Equal(new[] { false, false, true }, items.Select(item => item.Pinned));
        Assert.All(items, item => Assert.False(item.Saved));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnverifiedImportProtectsAllItemsAndPersistsWhenDatabaseExists(bool databaseAvailable)
    {
        var items = new[] { Item("one", false), Item("two", false), Item("three", true) };
        Assert.True(WindowsImportPinPolicy.ProtectUnverified(items, false, databaseAvailable));
        Assert.All(items, item => { Assert.True(item.Pinned); Assert.Equal(databaseAvailable, item.Saved); });
        Assert.False(WindowsImportPinPolicy.ProtectUnverified([], false, databaseAvailable));
    }
}
