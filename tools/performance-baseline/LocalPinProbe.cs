// LocalPinProbe.cs
// Read-only local diagnostic: emit fixed status categories, never IDs or data.
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;
using Windows.ApplicationModel.DataTransfer;
using System.Text.Json;

internal static class LocalPinProbe
{
    public static async Task RunAsync()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Clipboard", "Pinned");
            var trusted = PinnedHistorySnapshotReader.TryRead(root, out var pinnedIds);
            if (!trusted) Console.WriteLine("pin_probe_encoding_" + DetectEncodingMarker(root));
            if (!trusted) Console.WriteLine("pin_probe_reason_" + ExplainUntrusted(root));
            var history = await Clipboard.GetHistoryItemsAsync();
            if (history.Status != ClipboardHistoryItemsResultStatus.Success)
            {
                Console.WriteLine("pin_probe_history_unavailable");
                return;
            }
            Console.WriteLine(trusted ? "pin_probe_trusted" : "pin_probe_untrusted");
            Console.WriteLine(history.Items.Count == 0 ? "pin_probe_history_empty" : "pin_probe_history_nonempty");
            Console.WriteLine(history.Items.Any(item => pinnedIds.Contains(item.Id))
                ? "pin_probe_pinned_match" : "pin_probe_no_pinned_match");
        }
        catch (Exception)
        {
            Console.WriteLine("pin_probe_history_unavailable");
        }
    }
    private static string DetectEncodingMarker(string root)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                using var stream = File.OpenRead(Path.Combine(directory, "metadata.json"));
                var bytes = new byte[4];
                var count = stream.Read(bytes);
                if (count >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0) return "utf32le";
                if (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff) return "utf32be";
                if (count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) return "utf8";
                if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return "utf16le";
                if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return "utf16be";
                if (count >= 2 && bytes[0] == (byte)'{' && bytes[1] == 0) return "utf16le_unmarked";
                if (count >= 2 && bytes[0] == 0 && bytes[1] == (byte)'{') return "utf16be_unmarked";
                return "none";
            }
        }
        catch (Exception) { }
        return "unavailable";
    }

    private static string ExplainUntrusted(string root)
    {
        try
        {
            _ = File.GetAttributes(root);
            if (Directory.EnumerateFiles(root).Any()) return "root_files";
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                using var stream = File.OpenRead(Path.Combine(directory, "metadata.json"));
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return "root_shape";
                if (!document.RootElement.TryGetProperty("Items", out var items) &&
                    !document.RootElement.TryGetProperty("items", out items)) return "missing_items";
                if (items.ValueKind != JsonValueKind.Object) return "items_shape";
            }
            return "recovered";
        }
        catch (DirectoryNotFoundException) { return "directory_missing"; }
        catch (FileNotFoundException) { return "file_missing"; }
        catch (UnauthorizedAccessException) { return "access"; }
        catch (JsonException) { return "json"; }
        catch (IOException) { return "io"; }
        catch (Exception) { return "other"; }
    }

}
