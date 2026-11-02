// CleanupKeywordValidator.cs
using System;
using System.Linq;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

public enum CleanupKeywordValidation
{
    Valid,
    EmptyOrMultipleTerms,
    ConflictsWithClear
}

public static class CleanupKeywordValidator
{
    public static CleanupKeywordValidation Validate(string? cleanupKeyword, string? clearKeyword)
    {
        if (string.IsNullOrWhiteSpace(cleanupKeyword) || cleanupKeyword.Any(char.IsWhiteSpace))
        {
            return CleanupKeywordValidation.EmptyOrMultipleTerms;
        }
        return string.Equals(cleanupKeyword, clearKeyword?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? CleanupKeywordValidation.ConflictsWithClear
            : CleanupKeywordValidation.Valid;
    }

    public static bool IsExactQuery(string? search, string? cleanupKeyword) =>
        !string.IsNullOrWhiteSpace(cleanupKeyword) &&
        string.Equals(search?.Trim(), cleanupKeyword, StringComparison.OrdinalIgnoreCase);
}
