// ManagedCacheFileTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class ManagedCacheFileTests
{
    [Fact]
    public void OnlyOwnedByteIdenticalUnreferencedFilesCanBeDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var candidate = Path.Combine(root, "owned.png");
            var keeper = Path.Combine(root, "keeper.png");
            OwnedCacheFile? ownership;
            using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(new byte[] { 1, 2, 3 }); stream.Flush();
                ownership = ManagedCacheFile.IdentifyNewFile(root, candidate, stream);
            }
            File.WriteAllBytes(keeper, new byte[] { 1, 2, 3 });
            Assert.True(ManagedCacheFile.TryFingerprint(root, candidate, out var candidateSize, out _, out var candidateHash));
            Assert.True(ManagedCacheFile.TryFingerprint(root, keeper, out var keeperSize, out _, out var keeperHash));
            Assert.Equal(candidateSize, keeperSize);
            Assert.Equal(candidateHash, keeperHash);
            Assert.NotNull(ownership);
            using (var prepared = ManagedCacheFile.Prepare(root, ownership, keeper))
            {
                Assert.NotNull(prepared);
                Assert.False(prepared.DeleteIf(() => false));
                Assert.True(File.Exists(candidate));
                Assert.True(prepared.DeleteIf(() => true));
            }
            Assert.False(File.Exists(candidate));
            Assert.True(File.Exists(keeper));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ChangedContentOrInvalidOwnershipIsNeverDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var candidate = Path.Combine(root, "owned.png");
            var keeper = Path.Combine(root, "keeper.png");
            OwnedCacheFile? ownership;
            using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.WriteByte(1); stream.Flush();
                ownership = ManagedCacheFile.IdentifyNewFile(root, candidate, stream);
            }
            File.WriteAllBytes(keeper, new byte[] { 2 });
            Assert.NotNull(ownership);
            Assert.Null(ManagedCacheFile.Prepare(root, ownership, keeper));
            Assert.Null(ManagedCacheFile.Prepare(root, ownership with { FileName = "../owned.png" }, keeper));
            Assert.True(File.Exists(candidate));
        }
        finally { Directory.Delete(root, true); }
    }
}
