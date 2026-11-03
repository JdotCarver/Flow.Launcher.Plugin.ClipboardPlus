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
    public void LowercaseItemsWithNoPinsIsTrusted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-pins-{Guid.NewGuid():N}");
        try
        {
            var container = Path.Combine(root, "synthetic-container");
            Directory.CreateDirectory(container);
            File.WriteAllText(Path.Combine(container, "metadata.json"), "{\"items\":{}}");
            Assert.True(PinnedHistorySnapshotReader.TryRead(root, out var ids));
            Assert.Empty(ids);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16MetadataPreservesRealPinAndUnpinnedStates(bool bigEndian)
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-pins-{Guid.NewGuid():N}");
        try
        {
            var container = Path.Combine(root, "synthetic-container");
            Directory.CreateDirectory(container);
            var metadata = Path.Combine(container, "metadata.json");
            var encoding = new System.Text.UnicodeEncoding(bigEndian, true, true);
            File.WriteAllText(metadata, "{\"items\":{}}", encoding);
            Assert.True(PinnedHistorySnapshotReader.TryRead(root, out var empty));
            Assert.Empty(empty);
            File.WriteAllText(metadata, "{\"items\":{\"SYNTHETIC-PIN\":{}}}", encoding);
            Assert.True(PinnedHistorySnapshotReader.TryRead(root, out var ids));
            Assert.Contains("synthetic-pin", ids);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Utf8BomIsAcceptedButMalformedUtf16IsUntrusted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-pins-{Guid.NewGuid():N}");
        try
        {
            var container = Path.Combine(root, "synthetic-container");
            Directory.CreateDirectory(container);
            var metadata = Path.Combine(container, "metadata.json");
            File.WriteAllText(metadata, "{\"items\":{}}", new System.Text.UTF8Encoding(true, true));
            Assert.True(PinnedHistorySnapshotReader.TryRead(root, out _));
            File.WriteAllBytes(metadata, [0xff, 0xfe, 0x7b]);
            Assert.False(PinnedHistorySnapshotReader.TryRead(root, out _));
            File.WriteAllBytes(metadata, [0xff, 0xfe, 0x00, 0x00, 0x7b, 0x00, 0x00, 0x00]);
            Assert.False(PinnedHistorySnapshotReader.TryRead(root, out _));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":null}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":\"synthetic\"}")]
    public void UnexpectedMetadataShapesRemainUntrusted(string metadata)
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-pins-{Guid.NewGuid():N}");
        try
        {
            var container = Path.Combine(root, "synthetic-container");
            Directory.CreateDirectory(container);
            File.WriteAllText(Path.Combine(container, "metadata.json"), metadata);
            Assert.False(PinnedHistorySnapshotReader.TryRead(root, out var ids));
            Assert.Empty(ids);
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
