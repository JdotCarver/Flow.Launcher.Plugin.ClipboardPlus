// ClipboardSubtitleExtensions.cs
namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Extensions;

public static class ClipboardSubtitleExtensions
{
    /// <summary>Builds an uncached subtitle with a caller-formatted timestamp.</summary>
    public static string GetSubtitleWithTimestamp(this ClipboardData data, string timestamp)
    {
        var value = string.IsNullOrEmpty(data.SenderApp) ? timestamp : $"{timestamp}: {data.SenderApp}";
        return data.Pinned ? $"📌{value}" : value;
    }
}
