// PinnedHistorySnapshotReaderTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class PinnedHistorySnapshotReaderTests
{
    [Fact]
    public void SyntheticPinnedMetadataIsReadCaseInsensitively()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-pins-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "item"));
            File.WriteAllText(Path.Combine(root, "item", "metadata.json"), "{\"Items\":{\"SYNTHETIC-ID\":{}}}");
            Assert.True(PinnedHistorySnapshotReader.TryRead(root, out var ids));
            Assert.Contains("synthetic-id", ids);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingFolderAndMalformedOrMissingMetadataAreUntrusted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-pins-{Guid.NewGuid():N}");
        try
        {
            Assert.False(PinnedHistorySnapshotReader.TryRead(root, out _));
            Directory.CreateDirectory(root);
            Assert.True(PinnedHistorySnapshotReader.TryRead(root, out var empty));
            Assert.Empty(empty);
            File.WriteAllText(Path.Combine(root, "unexpected"), "synthetic");
            Assert.False(PinnedHistorySnapshotReader.TryRead(root, out _));
            File.Delete(Path.Combine(root, "unexpected"));
            Directory.CreateDirectory(Path.Combine(root, "item"));
            Assert.False(PinnedHistorySnapshotReader.TryRead(root, out _));
            File.WriteAllText(Path.Combine(root, "item", "metadata.json"), "not json");
            Assert.False(PinnedHistorySnapshotReader.TryRead(root, out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
