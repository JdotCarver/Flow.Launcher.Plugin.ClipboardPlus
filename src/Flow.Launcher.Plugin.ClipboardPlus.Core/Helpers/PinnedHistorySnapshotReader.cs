// PinnedHistorySnapshotReader.cs
using System;
using System.Collections.Generic;
using System.IO;
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
            try { _ = File.GetAttributes(pinnedFolder); }
            catch (DirectoryNotFoundException) { pinnedIds = ids; return true; }
            catch (FileNotFoundException) { pinnedIds = ids; return true; }
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
