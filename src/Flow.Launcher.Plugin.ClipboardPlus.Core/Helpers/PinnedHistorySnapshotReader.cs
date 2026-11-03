// PinnedHistorySnapshotReader.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Reads Windows pin metadata without exposing paths or clipboard content.</summary>
public static class PinnedHistorySnapshotReader
{
    public static bool TryRead(string pinnedFolder, out HashSet<string> pinnedIds)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // A missing folder is ambiguous: it can mean no metadata or an
            // unavailable/incomplete snapshot, not proof of zero pins.
            _ = File.GetAttributes(pinnedFolder);
            if (Directory.EnumerateFiles(pinnedFolder).Any())
            {
                pinnedIds = ids;
                return false;
            }
            foreach (var directory in Directory.EnumerateDirectories(pinnedFolder))
            {
                var metadataPath = Path.Combine(directory, "metadata.json");
                using var stream = File.OpenRead(metadataPath);
                using var document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("Items", out var items) &&
                    !document.RootElement.TryGetProperty("items", out items))
                {
                    pinnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    return false;
                }
                if (items.ValueKind != JsonValueKind.Object)
                {
                    pinnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    return false;
                }
                foreach (var item in items.EnumerateObject()) ids.Add(item.Name.ToLowerInvariant());
            }
            pinnedIds = ids;
            return true;
        }
        catch (Exception)
        {
            pinnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }
}
