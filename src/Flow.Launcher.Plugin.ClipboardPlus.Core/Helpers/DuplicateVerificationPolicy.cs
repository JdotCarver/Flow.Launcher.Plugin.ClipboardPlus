// DuplicateVerificationPolicy.cs
using System;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Provisional best-effort bound, subject to integrated Windows measurements.</summary>
public static class DuplicateVerificationPolicy
{
    public const int BestEffortCharacterLimit = 4096;

    public static bool CanVerifyBestEffort(ClipboardData data)
    {
        switch (data.DataType)
        {
            case DataType.PlainText:
                return data.Data is string text && text.Length <= BestEffortCharacterLimit;
            case DataType.RichText:
                return data.Data is string rtf && data.PlainText is not null &&
                    rtf.Length <= BestEffortCharacterLimit &&
                    data.PlainText.Length <= BestEffortCharacterLimit - rtf.Length;
            case DataType.Files:
                if (data.Data is not string[] paths || paths.Length > BestEffortCharacterLimit) return false;
                var remaining = BestEffortCharacterLimit - paths.Length;
                foreach (var path in paths)
                {
                    if (path is null || path.Length > remaining) return false;
                    remaining -= path.Length;
                }
                return true;
            default:
                // Images are expensive to encode; unknown content is not verifiable.
                return false;
        }
    }
}
