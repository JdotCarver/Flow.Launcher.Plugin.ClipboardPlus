// PinnedHistorySnapshotReader.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                using var document = JsonDocument.Parse(ReadMetadataJson(stream));
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
    private static string ReadMetadataJson(FileStream stream)
    {
        // Windows can write this JSON as UTF-16. JsonDocument's stream
        // overload accepts UTF-8 only, so decode recognized BOMs explicitly.
        // Throw on invalid bytes instead of replacing them and trusting damage.
        Span<byte> prefix = stackalloc byte[4];
        var count = stream.Read(prefix);
        Encoding encoding = new UTF8Encoding(false, true);
        var offset = 0;
        if (count >= 4 && ((prefix[0] == 0xff && prefix[1] == 0xfe && prefix[2] == 0 && prefix[3] == 0) ||
                           (prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff)))
            throw new InvalidDataException("Unsupported metadata encoding");
        if (count >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf)
            offset = 3;
        else if (count >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe)
        {
            encoding = new UnicodeEncoding(false, false, true);
            offset = 2;
        }
        else if (count >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff)
        {
            encoding = new UnicodeEncoding(true, false, true);
            offset = 2;
        }
        stream.Position = offset;
        using var reader = new StreamReader(stream, encoding, false, 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

}
