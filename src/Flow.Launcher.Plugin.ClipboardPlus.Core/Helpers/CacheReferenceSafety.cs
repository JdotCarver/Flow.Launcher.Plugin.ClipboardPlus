// CacheReferenceSafety.cs
using System;
using System.Collections.Generic;
using System.IO;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Conservatively rejects aliases and uncertain paths when checking cache references.</summary>
public static class CacheReferenceSafety
{
    public static bool IsUnreferenced(string candidate, IEnumerable<string> references)
    {
        try
        {
            if (!IsUnambiguous(candidate)) return false;
            var expected = Path.GetFullPath(candidate);
            var directory = Path.GetDirectoryName(expected);
            foreach (var reference in references)
            {
                if (string.IsNullOrEmpty(reference)) continue;
                if (!IsUnambiguous(reference)) return false;
                var full = Path.GetFullPath(reference);
                // Different directory spellings may be junctions or mapped-drive aliases.
                if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool IsUnambiguous(string path) =>
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
        !path.StartsWith(@"\\.\", StringComparison.Ordinal) && !path.Contains('~') &&
        !Path.GetFileName(path).Contains(':');
}
