// CacheReferenceSafetyTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class CacheReferenceSafetyTests
{
    [Fact]
    public void AliasesAndUnknownDirectoriesFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "synthetic-cache");
        var candidate = Path.Combine(root, "candidate.png");
        Assert.True(CacheReferenceSafety.IsUnreferenced(candidate, new[] { Path.Combine(root, "other.png") }));
        Assert.False(CacheReferenceSafety.IsUnreferenced(candidate, new[] { candidate.ToUpperInvariant() }));
        Assert.False(CacheReferenceSafety.IsUnreferenced(candidate, new[] { "relative.png" }));
        Assert.False(CacheReferenceSafety.IsUnreferenced(candidate, new[] { Path.Combine(root, "CANDID~1.PNG") }));
        Assert.False(CacheReferenceSafety.IsUnreferenced(candidate, new[] { Path.Combine(root, "candidate.png:stream") }));
        Assert.False(CacheReferenceSafety.IsUnreferenced(candidate, new[] { Path.Combine(root, "other", "candidate.png") }));
    }
}
