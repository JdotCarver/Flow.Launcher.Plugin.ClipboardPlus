// CleanupKeywordValidatorTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class CleanupKeywordValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("two words")]
    [InlineData("cleanup ")]
    public void RejectsEmptyOrMultipleTerms(string? keyword) =>
        Assert.Equal(CleanupKeywordValidation.EmptyOrMultipleTerms, CleanupKeywordValidator.Validate(keyword, "clear"));

    [Theory]
    [InlineData("clear", "clear")]
    [InlineData("CLEAR", "clear")]
    [InlineData("clear", " clear ")]
    public void RejectsClearKeywordConflicts(string keyword, string clear) =>
        Assert.Equal(CleanupKeywordValidation.ConflictsWithClear, CleanupKeywordValidator.Validate(keyword, clear));

    [Fact]
    public void MatchesOnlyTheExactQuery()
    {
        Assert.True(CleanupKeywordValidator.IsExactQuery(" cleanup ", "cleanup"));
        Assert.False(CleanupKeywordValidator.IsExactQuery("cleanup extra", "cleanup"));
        Assert.False(CleanupKeywordValidator.IsExactQuery("clean", "cleanup"));
        Assert.Equal(CleanupKeywordValidation.Valid, CleanupKeywordValidator.Validate("cleanup", "clear"));
    }
}
